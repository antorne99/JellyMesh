using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// Moves the data of 0.4 and 0.5 into the plugin database once, at its first healthy open, before any service uses it
/// (§10 rows 426, 469). The configuration XML, the seven JSON files and <c>watch-state.db</c> go in one transaction with
/// the mark of the migration; credentials go as they are, still encrypted. Then the old files move to
/// <c>migrated-0.5/</c>, which the plugin never reads, and which is erased 30 days later if the database is sound (D25).
/// </summary>
public sealed class LegacyImport
{
    /// <summary>The folder of the data folder that holds the old files after the migration.</summary>
    public const string FolderName = "migrated-0.5";

    /// <summary>The file name of the configuration XML Jellyfin kept for 0.4 and 0.5, in its plugin configurations folder.</summary>
    public const string ConfigurationFileName = "Jellyfin.Plugin.JellyMesh.xml";

    /// <summary>The file name of the 0.5 federation index.</summary>
    public const string IndexFileName = "federation-index.json";

    /// <summary>The file name of the 0.5 stub registry.</summary>
    public const string StubFileName = "federated-stubs.json";

    /// <summary>The schema version of the mark of the migration.</summary>
    public const int SchemaVersion = 5;

    /// <summary>How long <see cref="FolderName"/> is kept after the migration (D25).</summary>
    public static readonly TimeSpan MigratedKept = TimeSpan.FromDays(30);

    private const int CurrentVersion = 1;

    // SQLITE_CORRUPT and SQLITE_NOTADB: watch-state.db is not a database SQLite can read.
    private const int Corrupt = 11;
    private const int NotADatabase = 26;

    private const string SelectMark = "SELECT migrated_utc, moved_utc FROM legacy_import WHERE id = 1;";

    private const string InsertMark = "INSERT INTO legacy_import (id, migrated_utc, damaged_files) VALUES (1, $migrated, $damaged);";

    private const string MarkMoved = "UPDATE legacy_import SET moved_utc = $moved WHERE id = 1;";

    private const string MarkDeleted = "UPDATE legacy_import SET deleted_utc = $deleted WHERE id = 1;";

    private static readonly string[] _dataFiles =
    [
        IndexFileName,
        StubFileName,
        ManualMergeStore.FileName,
        LibraryRenameJournal.FileName,
        WatchStateCarryLog.FileName,
        LastServedSourceStore.FileName,
        SyncHistoryStore.FileName,
        KeptWatchStateStore.FileName
    ];

    // The options each 0.5 store read its file with, which differ from store to store.
    private static readonly JsonSerializerOptions _indexOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    private static readonly JsonSerializerOptions _stubOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions _mergeOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    private static readonly JsonSerializerOptions _renameOptions = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions _plainOptions = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    private static readonly JsonSerializerOptions _historyOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _folder;
    private readonly string? _configurationFile;
    private readonly ILogger<LegacyImport> _logger;
    private readonly IKeyFileGuard _guard;
    private readonly Func<SqliteConnection, string> _check;

    /// <summary>
    /// Initializes a new instance of the <see cref="LegacyImport"/> class.
    /// </summary>
    /// <param name="folder">The plugin data folder.</param>
    /// <param name="configurationFile">The configuration XML Jellyfin kept for 0.5, or null when there is none to read.</param>
    /// <param name="logger">The logger.</param>
    public LegacyImport(string folder, string? configurationFile, ILogger<LegacyImport> logger)
        : this(folder, configurationFile, logger, KeyFileGuards.ForThisSystem())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LegacyImport"/> class with its own way of keeping files private.
    /// </summary>
    /// <param name="folder">The plugin data folder.</param>
    /// <param name="configurationFile">The configuration XML, or null.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="guard">Keeps the moved files private.</param>
    /// <param name="check">Runs the integrity check of the database; <c>PRAGMA integrity_check</c> when null.</param>
    internal LegacyImport(string folder, string? configurationFile, ILogger<LegacyImport> logger, IKeyFileGuard guard, Func<SqliteConnection, string>? check = null)
    {
        _folder = folder;
        _configurationFile = configurationFile;
        _logger = logger;
        _guard = guard;
        _check = check ?? FullCheck;
    }

    private string MigratedFolder => Path.Combine(_folder, FolderName);

    /// <summary>
    /// Creates the table of the mark, inside the transaction of a schema step.
    /// </summary>
    /// <param name="connection">The writer connection.</param>
    /// <param name="transaction">The transaction of the step.</param>
    public static void CreateTable(SqliteConnection connection, SqliteTransaction transaction) => StoreCommands.Execute(
        connection,
        transaction,
        """
        CREATE TABLE legacy_import (
            id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            migrated_utc INTEGER NOT NULL,
            damaged_files TEXT NOT NULL,
            moved_utc INTEGER,
            deleted_utc INTEGER);
        """);

    /// <summary>
    /// Imports the old files and marks the migration, the first time; then moves the old files away, which a later open
    /// finishes if it was cut short. Files that come back after the move are left in place. Called with the database gate held.
    /// </summary>
    /// <param name="writer">The writer connection of the database.</param>
    /// <param name="now">The time of the migration.</param>
    /// <exception cref="IOException">An old file cannot be read for now; nothing is imported, and the next open tries again.</exception>
    internal void Run(SqliteConnection writer, DateTimeOffset now)
    {
        var files = OldFiles();
        var mark = ReadMark(writer);
        if (mark is null)
        {
            var damaged = new List<string>();
            var data = Read(damaged);
            using (var transaction = writer.BeginTransaction(deferred: false))
            {
                Write(writer, transaction, data);
                StoreSql.Execute(writer, transaction, InsertMark, ("$migrated", now.UtcTicks), ("$damaged", JsonSerializer.Serialize(damaged)));
                transaction.Commit();
            }

            foreach (var file in damaged)
            {
                _logger.LogWarning("The old JellyMesh file {File} was damaged, so it was read as the older version would have: from its previous copy, or empty. It stays in {Folder}", file, FolderName);
            }

            if (files.Count > 0)
            {
                _logger.LogInformation("The data of the older JellyMesh version moved into the plugin database. The old files are kept in {Folder} for 30 days", FolderName);
            }
        }
        else if (mark.Value.Moved)
        {
            if (files.Count > 0)
            {
                _logger.LogWarning("Files of the older JellyMesh version came back after the migration and are neither read nor moved: {Files}", string.Join(", ", files.Select(Path.GetFileName)));
            }

            return;
        }

        if (files.Count == 0 || Move(files))
        {
            using var transaction = writer.BeginTransaction(deferred: false);
            StoreSql.Execute(writer, transaction, MarkMoved, ("$moved", now.UtcTicks));
            transaction.Commit();
        }
    }

    /// <summary>
    /// Overwrites and deletes <see cref="FolderName"/> once 30 days have passed since the migration and a fresh integrity
    /// check passes (D25). Called with the database gate held, on the writer.
    /// </summary>
    /// <param name="writer">The writer connection of the database.</param>
    /// <param name="now">The current time.</param>
    /// <returns>Whether the folder is gone.</returns>
    internal bool DeleteExpired(SqliteConnection writer, DateTimeOffset now)
    {
        if (!Directory.Exists(MigratedFolder))
        {
            return true;
        }

        if (IsLinkedFolder())
        {
            return false;
        }

        if (ReadMark(writer) is not { } mark || now - mark.MigratedUtc < MigratedKept)
        {
            return false;
        }

        if (!string.Equals(_check(writer), "ok", StringComparison.Ordinal))
        {
            _logger.LogWarning("The JellyMesh database failed its integrity check, so {Folder} is kept", FolderName);
            return false;
        }

        var kept = 0;
        // Only the top level holds old files; a link there is removed without following it.
        foreach (var entry in new DirectoryInfo(MigratedFolder).EnumerateFileSystemInfos().ToList())
        {
            var file = entry.FullName;
            try
            {
                if (entry is FileInfo || entry.LinkTarget is not null)
                {
                    StoreBackups.Erase(file);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                kept++;
                _logger.LogWarning("The old JellyMesh file {File} cannot be overwritten and deleted yet: {Error}", Path.GetFileName(file), exception.Message);
            }
        }

        if (kept > 0)
        {
            return false;
        }

        Directory.Delete(MigratedFolder, recursive: true);
        using (var transaction = writer.BeginTransaction(deferred: false))
        {
            StoreSql.Execute(writer, transaction, MarkDeleted, ("$deleted", now.UtcTicks));
            transaction.Commit();
        }

        _logger.LogInformation("The old JellyMesh files in {Folder} were overwritten and deleted, 30 days after the migration", FolderName);
        return true;
    }

    /// <summary>
    /// Gets until when <see cref="FolderName"/> is kept: 30 days after the migration, while it is there and not a link.
    /// </summary>
    /// <param name="writer">The writer connection of the database.</param>
    /// <returns>The date, or <c>null</c>.</returns>
    internal DateTimeOffset? KeptUntil(SqliteConnection writer)
    {
        var folder = new DirectoryInfo(MigratedFolder);
        return folder.Exists && folder.LinkTarget is null && ReadMark(writer) is { } mark ? mark.MigratedUtc + MigratedKept : null;
    }

    // A folder for the old files that is a link is never written, made private or erased through.
    private bool IsLinkedFolder()
    {
        if (new DirectoryInfo(MigratedFolder).LinkTarget is null)
        {
            return false;
        }

        _logger.LogWarning("{Folder} is a link, so the old JellyMesh files are neither moved into it nor erased from it", FolderName);
        return true;
    }

    private static string FullCheck(SqliteConnection writer)
    {
        using var check = writer.CreateCommand();
        check.CommandText = "PRAGMA integrity_check;";
        return Convert.ToString(check.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static (DateTimeOffset MigratedUtc, bool Moved)? ReadMark(SqliteConnection writer)
    {
        using var command = writer.CreateCommand();
        command.CommandText = SelectMark;
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero), !reader.IsDBNull(1))
            : null;
    }

    // Keeps the folder migration chosen in 0.5; an automatic one migrates again, from the addition of the paths (§10 row 444).
    private static void RememberLayoutChoice(LayoutMigrationConfig migration)
    {
        if (migration.Mode == LayoutMigrationMode.Automatic)
        {
            migration.Step = AutomaticMigrationStep.AddPaths;
            migration.Error = AutomaticMigrationError.None;
            migration.ErrorLibrary = string.Empty;
        }
    }

    private static void Write(SqliteConnection connection, SqliteTransaction transaction, LegacyData data)
    {
        if (data.Configuration is { } configuration)
        {
            RememberLayoutChoice(configuration.LayoutMigration);
            ConfigurationTables.Write(connection, transaction, ConfigurationTables.Read(connection, transaction), ConfigurationRows.From(configuration));
        }

        if (data.Index is { } index)
        {
            var servers = index.Servers.DistinctBy(server => server.ServerId)
                .Select(server => IndexTables.Plan(server.ServerId, null, server, null))
                .OfType<IndexTables.ServerWrite>()
                .ToList();
            IndexTables.Write(connection, transaction, servers, clear: true);
        }

        if (data.Stubs is not null && StubTables.Plan(null, data.Stubs) is { } stubs)
        {
            StubTables.Write(connection, transaction, stubs);
        }

        if (data.Merges is { } merges)
        {
            ManualMergeStore.Replace(connection, transaction, ManualMergeStore.ReadAll(connection, transaction), merges);
        }

        foreach (var record in data.Renames ?? [])
        {
            LibraryRenameJournal.InsertRecord(connection, transaction, record);
        }

        foreach (var carry in data.Carries ?? [])
        {
            StoreSql.Execute(
                connection,
                transaction,
                "INSERT OR REPLACE INTO watch_state_carries (user_id, item_id, last_played_ticks) VALUES ($user, $item, $played);",
                ("$user", StoreSql.Text(carry.UserId)),
                ("$item", StoreSql.Text(carry.ItemId)),
                ("$played", carry.LastPlayedDate?.Ticks));
        }

        foreach (var served in data.LastServed ?? [])
        {
            StoreSql.Execute(
                connection,
                transaction,
                "INSERT OR REPLACE INTO last_served_sources (user_id, stub_id, server_id) VALUES ($user, $stub, $server);",
                ("$user", StoreSql.Text(served.UserId)),
                ("$stub", served.StubId),
                ("$server", StoreSql.Text(served.ServerId)));
        }

        if (data.History is { } history)
        {
            SyncHistoryStore.Save(connection, transaction, history.ToList());
        }

        LegacyKeptRows.Write(connection, transaction, data.Kept ?? []);
    }

    // The old files in the data folder, with their copies, logs and damaged copies, and the configuration XML.
    private List<string> OldFiles()
    {
        var files = new List<string>();
        if (Directory.Exists(_folder))
        {
            files.AddRange(Directory.EnumerateFiles(_folder)
                .Where(path => _dataFiles.Any(name => Path.GetFileName(path).StartsWith(name, StringComparison.Ordinal))));
        }

        if (_configurationFile is not null && File.Exists(_configurationFile))
        {
            files.Add(_configurationFile);
        }

        return files;
    }

    // Moves each file over any copy a move cut short left, then keeps it and the folder private (row 428), never through
    // a link. Returns whether every file moved.
    private bool Move(List<string> files)
    {
        try
        {
            var target = MigratedFolder;
            if (Directory.Exists(target) && IsLinkedFolder())
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                Directory.CreateDirectory(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            foreach (var file in files)
            {
                var moved = Path.Combine(target, Path.GetFileName(file));
                File.Move(file, moved, overwrite: true);
                if (new FileInfo(moved).LinkTarget is null)
                {
                    _guard.KeepPrivate(moved);
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The old JellyMesh files cannot all move to {Folder} yet: {Error}. They are never read again, and the next start moves them", FolderName, exception.Message);
            return false;
        }
    }

    private LegacyData Read(List<string> damaged)
    {
        var index = ReadJson<IndexSnapshot>(IndexFileName, _indexOptions, damaged, snapshot => snapshot.Version == IndexSnapshot.CurrentVersion && snapshot.Servers.All(server => server is not null));
        if (index is null && damaged.Contains(IndexFileName))
        {
            // The older version loaded the previous copy, with every server stale until it syncs again.
            var backup = ReadJson<IndexSnapshot>(IndexFileName + ".bak", _indexOptions, [], snapshot => snapshot.Version == IndexSnapshot.CurrentVersion && snapshot.Servers.All(server => server is not null));
            index = backup is null ? null : backup with { Servers = backup.Servers.Select(server => server with { State = ServerSyncState.Stale }).ToList() };
        }

        var stubs = ReadJson<StubState>(StubFileName, _stubOptions, damaged, state => state.Version is StubState.CurrentVersion or StubState.FlatLayoutVersion && WholeStubs(state));
        var merges = ReadJson<MergeFile>(ManualMergeStore.FileName, _mergeOptions, damaged, file => file.Version == CurrentVersion && file.Merges.All(merge => merge is not null));
        var renames = ReadJson<RenameFile>(LibraryRenameJournal.FileName, _renameOptions, damaged, file => file.Version == CurrentVersion && file.Records.All(record => record is not null));
        var carries = ReadJson<CarryFile>(WatchStateCarryLog.FileName, _plainOptions, damaged, file => file.Version == CurrentVersion && file.Entries.All(entry => entry is not null));
        var served = ReadJson<LastServedFile>(LastServedSourceStore.FileName, _plainOptions, damaged, file => file.Version == CurrentVersion && file.Entries.All(entry => entry is not null));
        var history = ReadJson<HistoryFile>(SyncHistoryStore.FileName, _historyOptions, damaged, file => file.Version == CurrentVersion && file.Runs.All(run => run is not null && run.Servers is not null && run.Servers.All(server => server is not null)));

        // A file that is missing or damaged leaves its tables as they are, which on a new database is the empty state the
        // older version started from.
        return new LegacyData(
            ReadConfiguration(damaged),
            index,
            stubs is null ? null : stubs with
            {
                Version = StubState.CurrentVersion,
                Stubs = stubs.Stubs.DistinctBy(stub => stub.Id).ToList(),
                Series = stubs.Series.DistinctBy(series => series.Id).ToList()
            },
            merges?.Merges.DistinctBy(merge => merge.Id).ToList(),
            renames?.Records,
            carries?.Entries.DistinctBy(entry => (entry.UserId, entry.ItemId)).ToList(),
            served?.Entries.DistinctBy(entry => (entry.UserId, entry.StubId)).ToList(),
            history?.Runs,
            ReadKept(damaged));
    }

    // Whether every list and entry the stub tables write is there, as the older version wrote them.
    private static bool WholeStubs(StubState state) =>
        state.Stubs is not null && state.Series is not null && state.Groups is not null && state.Labels is not null
        && state.RemovedServers is not null && state.ServerNames is not null && state.Renames is not null
        && state.Stubs.All(stub => stub is { Id: not null, RelativePath: not null, IdentityKey: not null, Sources: not null, PartCopies: not null } && WholeSources(stub.Sources)
            && stub.PartCopies.All(item => item is not null))
        && state.Series.All(series => series is { Id: not null, RelativePath: not null, WorkKey: not null, Sources: not null, Seasons: not null }
            && WholeSources(series.Sources) && series.Seasons.All(season => season is { RelativePath: not null }))
        && state.Groups.All(group => group is { Id: not null, IdentityKey: not null, StubIds: not null, LocalItemIds: not null } && group.StubIds.All(id => id is not null))
        && state.Labels.Values.All(label => label is not null) && state.ServerNames.Values.All(name => name is not null)
        && state.Renames.All(rename => rename is { StubId: not null, OldPath: not null })
        && state.Splits is not null && state.Splits.All(split => split is { StubId: not null, FromStubId: not null });

    private static bool WholeSources(IReadOnlyList<StubSource> sources) =>
        sources.All(source => source is { ItemId: not null, MediaSourceId: not null });

    // Null when the file is missing or damaged; damaged files are named in the list. A file that cannot be read for now throws.
    private T? ReadJson<T>(string name, JsonSerializerOptions options, List<string> damaged, Func<T, bool> whole)
        where T : class
    {
        var path = Path.Combine(_folder, name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (JsonSerializer.Deserialize<T>(stream, options) is { } value && whole(value))
            {
                return value;
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            _logger.LogDebug("The old JellyMesh file {File} cannot be read: {Error}", name, exception.GetType().Name);
        }

        damaged.Add(name);
        return null;
    }

    // Jellyfin starts a plugin with a configuration XML it cannot read from the defaults, and so does the migration. The
    // bytes are read first, so a file that cannot be read for now throws instead of counting as damaged.
    private PluginConfiguration? ReadConfiguration(List<string> damaged)
    {
        if (_configurationFile is null || !File.Exists(_configurationFile))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(_configurationFile);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            if (new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader) is PluginConfiguration configuration)
            {
                return configuration;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or XmlException)
        {
            _logger.LogDebug("The old JellyMesh configuration cannot be read: {Error}", exception.GetType().Name);
        }

        damaged.Add(Path.GetFileName(_configurationFile));
        return null;
    }

    private List<LegacyKeptRows.Title>? ReadKept(List<string> damaged)
    {
        var path = Path.Combine(_folder, KeptWatchStateStore.FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        // ReadWrite and never Create, so a log a crash left is rolled back as the older version would have.
        var settings = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        try
        {
            using var connection = new SqliteConnection(settings);
            connection.Open();
            return LegacyKeptRows.Read(connection);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is Corrupt or NotADatabase)
        {
            _logger.LogDebug("The old JellyMesh file {File} cannot be read: {Error}", KeptWatchStateStore.FileName, exception.Message);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            _logger.LogDebug("The old JellyMesh file {File} holds a row that cannot be read: {Error}", KeptWatchStateStore.FileName, exception.GetType().Name);
        }

        damaged.Add(KeptWatchStateStore.FileName);
        return null;
    }

    private sealed record LegacyData(
        PluginConfiguration? Configuration,
        IndexSnapshot? Index,
        StubState? Stubs,
        IReadOnlyList<ManualMerge>? Merges,
        IReadOnlyList<LibraryRenameRecord>? Renames,
        IReadOnlyList<CarryEntry>? Carries,
        IReadOnlyList<LastServedEntry>? LastServed,
        IReadOnlyList<SyncRun>? History,
        IReadOnlyList<LegacyKeptRows.Title>? Kept);

    private sealed record MergeFile(int Version, IReadOnlyList<ManualMerge> Merges);

    private sealed record RenameFile(int Version, IReadOnlyList<LibraryRenameRecord> Records);

    private sealed record CarryEntry(Guid UserId, Guid ItemId, DateTime? LastPlayedDate);

    private sealed record CarryFile(int Version, IReadOnlyList<CarryEntry> Entries);

    private sealed record LastServedEntry(Guid UserId, string StubId, Guid ServerId);

    private sealed record LastServedFile(int Version, IReadOnlyList<LastServedEntry> Entries);

    private sealed record HistoryFile(int Version, IReadOnlyList<SyncRun> Runs);
}
