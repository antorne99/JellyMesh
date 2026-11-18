using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Storage;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using Jellyfin.Data.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Storage;

// The files are those a 0.5.0.1 server wrote after a sync of one stock remote.
public sealed class LegacyImportTests : IDisposable
{
    private const string FixtureCredentials = "AQTULwxQzPHzkKaBIHjBRQbfnffaMwQLkOKhoSp1uKJ35QCnrQEYe4BgpSMbLzz6T6pUmWtFIcUOf2+bqQs1i/37eMKbj69kfxEOOQTjsGM+";

    private static readonly Guid _fixtureServer = Guid.Parse("8a4800ba-4770-4936-b019-94e846e237bb");

    private static readonly string[] _jsonFiles =
    [
        "federation-index.json", "federated-stubs.json", "manual-merges.json", "library-renames.json",
        "watch-state-carries.json", "last-served.json", "sync-history.json"
    ];

    private readonly List<PluginDatabase> _opened = [];

    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 18, 3, 0, 0, TimeSpan.Zero));

    public LegacyImportTests()
    {
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationFile)!);
    }

    private string Root { get; } = Path.Combine(Path.GetTempPath(), "jellymesh-legacy-" + Guid.NewGuid().ToString("N"));

    private string Folder => Path.Combine(Root, "Jellyfin.Plugin.JellyMesh");

    private string ConfigurationFile => Path.Combine(Root, "configurations", "Jellyfin.Plugin.JellyMesh.xml");

    private string Migrated => Path.Combine(Folder, LegacyImport.FolderName);

    private CapturingLogger<LegacyImport> Logger { get; } = new();

    public void Dispose()
    {
        foreach (var database in _opened)
        {
            database.Dispose();
        }

        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Storage", "Legacy05", name);

    private void CopyFixtures()
    {
        File.Copy(Fixture("Jellyfin.Plugin.JellyMesh.xml"), ConfigurationFile);
        foreach (var name in new[] { "federation-index.json", "federated-stubs.json", "sync-history.json" })
        {
            File.Copy(Fixture(name), Path.Combine(Folder, name));
        }
    }

    private string QuickCheckResult { get; set; } = "ok";

    private PluginDatabase Open(int busyTimeoutSeconds = 1)
    {
        var database = new PluginDatabase(
            Folder,
            new CapturingLogger<PluginDatabase>(),
            KeyFileGuards.ForThisSystem(),
            SchemaMigrations.All,
            busyTimeoutSeconds,
            _clock,
            new LegacyImport(Folder, ConfigurationFile, Logger, KeyFileGuards.ForThisSystem(), _ => QuickCheckResult));
        _opened.Add(database);
        _ = database.Health;
        return database;
    }

    private static Task<PluginConfiguration> ConfigurationOf(PluginDatabase database) =>
        database.ReadAsync((connection, transaction) => ConfigurationTables.Read(connection, transaction).ToConfiguration(), CancellationToken.None);

    private static Task<long> CountAsync(PluginDatabase database, string table) => database.ReadAsync(
        (connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM " + table + ";";
            return (long)command.ExecuteScalar()!;
        },
        CancellationToken.None);

    [Fact]
    public async Task TheDataOfVersion05MovesIntoTheDatabaseWithTheCredentialsAsTheyWere()
    {
        CopyFixtures();

        var database = Open();

        Assert.Equal(StoreState.Healthy, database.Health.State);
        var config = await ConfigurationOf(database);
        var server = Assert.Single(config.Servers);
        Assert.Equal(_fixtureServer, server.Id);
        Assert.Equal(FixtureCredentials, server.EncryptedCredentials);
        Assert.Equal("/federata", config.FederatedFolderPath);
        Assert.Equal(2, server.ImportedLibraryIds.Count);

        var index = await database.ReadAsync((connection, transaction) => IndexTables.Load(connection, transaction).Snapshot, CancellationToken.None);
        var catalog = Assert.Single(index.Servers);
        Assert.Equal(ServerSyncState.Complete, catalog.State);
        Assert.Equal(20, catalog.Items.Count);

        var stubs = await database.ReadAsync(StubTables.Load, CancellationToken.None);
        Assert.Equal(16, stubs.Stubs.Count);
        Assert.Equal(2, stubs.Series.Count);
        Assert.Equal(2, await CountAsync(database, "sync_runs"));
    }

    [Fact]
    public void AfterTheImportTheOldFilesAreKeptInTheirOwnFolderAndNoLongerInPlace()
    {
        CopyFixtures();
        File.Copy(Fixture("federation-index.json"), Path.Combine(Folder, "federation-index.json.bak"));

        Open();

        Assert.False(File.Exists(ConfigurationFile));
        foreach (var name in new[] { "federation-index.json", "federation-index.json.bak", "federated-stubs.json", "sync-history.json" })
        {
            Assert.False(File.Exists(Path.Combine(Folder, name)), name);
            Assert.True(File.Exists(Path.Combine(Migrated, name)), name);
        }

        Assert.True(File.Exists(Path.Combine(Migrated, "Jellyfin.Plugin.JellyMesh.xml")));
        Assert.True(File.Exists(Path.Combine(Folder, PluginDatabase.FileName)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Migrated));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(Migrated, "Jellyfin.Plugin.JellyMesh.xml")));
        }
    }

    [Fact]
    public async Task AnImportThatFailsMovesNothingSuspendsTheStoreAndTheNextOpenTriesAgain()
    {
        using (var before = new PluginDatabase(Folder, new CapturingLogger<PluginDatabase>()))
        {
            Assert.Equal(StoreState.Healthy, before.Health.State);
        }

        SqliteConnection.ClearAllPools();
        CopyFixtures();
        using var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Folder, PluginDatabase.FileName), Pooling = false }.ToString());
        await holder.OpenAsync();
        using (var begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync();
        }

        var database = Open();

        Assert.Equal(StoreState.Unavailable, database.Health.State);
        Assert.True(File.Exists(ConfigurationFile));
        Assert.True(File.Exists(Path.Combine(Folder, "federation-index.json")));
        Assert.False(Directory.Exists(Migrated));

        using (var rollback = holder.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
        }

        Assert.Equal(StoreState.Healthy, database.Retry().State);
        Assert.Equal(FixtureCredentials, Assert.Single((await ConfigurationOf(database)).Servers).EncryptedCredentials);
        Assert.False(File.Exists(ConfigurationFile));
    }

    // The small files as 0.5 wrote them, read back through the stores that now keep them in the database.
    [Fact]
    public async Task TheSmallFilesOfVersion05AreReadBackByTheirStores()
    {
        var (user, item, stub, server, merge) = (Guid.NewGuid(), Guid.NewGuid(), "f-" + Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid());
        var (oldLibrary, newLibrary) = (Guid.NewGuid(), Guid.NewGuid());
        await File.WriteAllTextAsync(Path.Combine(Folder, "watch-state-carries.json"), $$"""
            {"Version":1,"Entries":[{"UserId":"{{user}}","ItemId":"{{item}}","LastPlayedDate":"2026-10-01T20:00:00Z"}]}
            """);
        await File.WriteAllTextAsync(Path.Combine(Folder, "last-served.json"), $$"""
            {"Version":1,"Entries":[{"UserId":"{{user}}","StubId":"{{stub}}","ServerId":"{{server}}"}]}
            """);
        await File.WriteAllTextAsync(Path.Combine(Folder, "manual-merges.json"), $$"""
            {"Version":1,"Merges":[{"Id":"{{merge}}","CreatedUtc":"2026-10-02T10:00:00+00:00","Copies":[
              {"ServerId":"{{server:N}}","ItemId":"a1","Kind":0,"IsLocal":false,"Title":"Film","Year":2001},
              {"ServerId":"local","ItemId":"b2","Kind":0,"IsLocal":true,"Tmdb":"603"}]}]}
            """);
        await File.WriteAllTextAsync(Path.Combine(Folder, "library-renames.json"), $$"""
            {"Version":1,"Records":[{"OldName":"Film","NewName":"Film (JellyMesh)","OldId":"{{oldLibrary}}","NewId":"{{newLibrary}}","Type":"movies","TemporaryName":null}]}
            """);

        var database = Open();

        using var carries = new WatchStateCarryLog(database, new CapturingLogger<WatchStateCarryLog>());
        Assert.True(carries.EnsureLoaded());
        Assert.True(carries.TryFind(user, item, out var played));
        Assert.Equal(new DateTime(2026, 10, 1, 20, 0, 0, DateTimeKind.Utc), played);

        using var served = new LastServedSourceStore(database, new CapturingLogger<LastServedSourceStore>());
        Assert.Equal(server, served.Find(user, stub));

        using var merges = new ManualMergeStore(database, new CapturingLogger<ManualMergeStore>());
        var kept = Assert.Single(merges.Merges);
        Assert.Equal(merge, kept.Id);
        Assert.Equal(["a1", "b2"], kept.Copies.Select(copy => copy.ItemId));
        Assert.True(kept.Copies[1].IsLocal);

        var renames = new LibraryRenameJournal(database, new CapturingLogger<LibraryRenameJournal>());
        var rename = Assert.Single(renames.Pending());
        Assert.Equal(("Film", "Film (JellyMesh)", oldLibrary, newLibrary, CollectionType.movies), (rename.OldName, rename.NewName, rename.OldId, rename.NewId, rename.Type));
    }

    // A database a build before the migration filled has no mark: the import without old files leaves its rows alone.
    [Fact]
    public async Task ADatabaseWithoutTheMarkKeepsItsRowsWhenThereAreNoOldFiles()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var index = JsonSerializer.Deserialize<IndexSnapshot>(await File.ReadAllTextAsync(Fixture("federation-index.json")), options)!;
        var stubs = JsonSerializer.Deserialize<StubState>(await File.ReadAllTextAsync(Fixture("federated-stubs.json")), options)!;
        using (var before = new PluginDatabase(Folder, new CapturingLogger<PluginDatabase>()))
        {
            await before.WriteAsync(
                (connection, transaction) =>
                {
                    var catalog = index.Servers[0];
                    IndexTables.Write(connection, transaction, [IndexTables.Plan(catalog.ServerId, null, catalog, null)!], clear: false);
                    StubTables.Write(connection, transaction, StubTables.Plan(null, stubs)!);
                },
                CancellationToken.None);
        }

        SqliteConnection.ClearAllPools();

        var database = Open();

        Assert.Equal(20, await CountAsync(database, "index_items"));
        Assert.Equal(16, await CountAsync(database, "stub_entries"));
    }

    // XmlSerializer drops what it does not know: the 0.5 configuration must hold nothing the import would lose.
    [Fact]
    public void TheConfigurationOfVersion05HasNothingTheImportDrops()
    {
        var unknown = new List<string>();
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        serializer.UnknownElement += (_, e) => unknown.Add(e.Element.Name);
        serializer.UnknownAttribute += (_, e) => unknown.Add(e.Attr.Name);
        serializer.UnknownNode += (_, e) => unknown.Add(e.Name);

        using var stream = File.OpenRead(Fixture("Jellyfin.Plugin.JellyMesh.xml"));
        Assert.NotNull(serializer.Deserialize(stream));
        Assert.Empty(unknown);
    }

    [Fact]
    public async Task EachDamagedOldFileStartsEmptyAsTheOlderVersionDidWithAWarning()
    {
        foreach (var name in _jsonFiles.Append("watch-state.db"))
        {
            await File.WriteAllTextAsync(Path.Combine(Folder, name), "{ not what it should be");
        }

        await File.WriteAllTextAsync(ConfigurationFile, "<PluginConfiguration><Servers>");

        var database = Open();

        Assert.Equal(StoreState.Healthy, database.Health.State);
        Assert.Empty((await ConfigurationOf(database)).Servers);
        foreach (var table in new[] { "index_items", "stub_entries", "manual_merges", "library_renames", "watch_state_carries", "last_served_sources", "sync_runs", "kept_titles" })
        {
            Assert.Equal(0, await CountAsync(database, table));
        }

        Assert.Equal(_jsonFiles.Length + 2, Logger.Levels.Count(level => level == LogLevel.Warning));
        Assert.DoesNotContain(Directory.EnumerateFiles(Folder), path => _jsonFiles.Contains(Path.GetFileName(path)));
    }

    [Fact]
    public async Task ADamagedIndexFallsBackToItsCopyWithEveryServerStale()
    {
        await File.WriteAllTextAsync(Path.Combine(Folder, "federation-index.json"), "{");
        File.Copy(Fixture("federation-index.json"), Path.Combine(Folder, "federation-index.json.bak"));

        var database = Open();

        var index = await database.ReadAsync((connection, transaction) => IndexTables.Load(connection, transaction).Snapshot, CancellationToken.None);
        var catalog = Assert.Single(index.Servers);
        Assert.Equal(ServerSyncState.Stale, catalog.State);
        Assert.Equal(20, catalog.Items.Count);
    }

    [Fact]
    public async Task TheKeptWatchStateMovesWithItsKindByName()
    {
        var oldItem = Guid.NewGuid();
        var user = Guid.NewGuid();
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Folder, "watch-state.db"), Pooling = false }.ToString()))
        {
            await old.OpenAsync();
            using var command = old.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE Titles (Id INTEGER PRIMARY KEY, OldItemId TEXT NOT NULL UNIQUE, Kind INTEGER NOT NULL, Title TEXT, Year INTEGER,
                    Tmdb TEXT, Imdb TEXT, Tvdb TEXT, OriginalTitle TEXT, RunTimeTicks INTEGER, Overview TEXT, Genres TEXT NOT NULL,
                    Studios TEXT NOT NULL, People TEXT NOT NULL, SeasonNumber INTEGER, EpisodeNumber INTEGER, EpisodeNumberEnd INTEGER,
                    SavedUtc INTEGER NOT NULL);
                CREATE TABLE UserStates (TitleId INTEGER NOT NULL, UserId TEXT NOT NULL, Played INTEGER NOT NULL, PlayCount INTEGER NOT NULL,
                    PlaybackPositionTicks INTEGER NOT NULL, LastPlayedUtc INTEGER, IsFavorite INTEGER NOT NULL, Rating REAL,
                    PRIMARY KEY (TitleId, UserId));
                INSERT INTO Titles VALUES (7, '{oldItem:N}', 1, 'Pilot', 2020, '42', NULL, NULL, NULL, 100, NULL, '[]', '[]', '[]', 1, 1, NULL, 5);
                INSERT INTO UserStates VALUES (7, '{user:N}', 1, 3, 0, 9, 1, 8.5);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var database = Open();

        var rows = await database.ReadAsync(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT t.old_item_id, t.kind, t.title, u.user_id, u.play_count, u.rating FROM kept_titles t JOIN kept_user_states u ON u.title_id = t.id;";
                using var reader = command.ExecuteReader();
                var found = new List<string>();
                while (reader.Read())
                {
                    found.Add(string.Join('|', reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetDouble(5)));
                }

                return found;
            },
            CancellationToken.None);
        Assert.Equal([$"{oldItem:N}|Episode|Pilot|{user:N}|3|8.5"], rows);
    }

    // Files that come back after the migration, as a return to 0.5 would write them, are neither read nor moved over the copies.
    [Fact]
    public async Task OldFilesThatComeBackAfterTheImportStayWhereTheyAreWithOneWarning()
    {
        CopyFixtures();
        Open().Dispose();
        SqliteConnection.ClearAllPools();
        var kept = await File.ReadAllBytesAsync(Path.Combine(Migrated, "sync-history.json"));
        await File.WriteAllTextAsync(Path.Combine(Folder, "sync-history.json"), """{"Version":1,"Runs":[]}""");
        await File.WriteAllTextAsync(ConfigurationFile, "<PluginConfiguration />");
        Logger.Levels.Clear();

        var database = Open();

        Assert.Equal(2, await CountAsync(database, "sync_runs"));
        Assert.Single((await ConfigurationOf(database)).Servers);
        Assert.Equal("""{"Version":1,"Runs":[]}""", await File.ReadAllTextAsync(Path.Combine(Folder, "sync-history.json")));
        Assert.True(File.Exists(ConfigurationFile));
        Assert.Equal(kept, await File.ReadAllBytesAsync(Path.Combine(Migrated, "sync-history.json")));
        Assert.Equal([LogLevel.Warning], Logger.Levels.Where(level => level >= LogLevel.Warning));
    }

    // Each move is a rename, so a start cut short after the import leaves only files without a copy, which the next start moves.
    [Fact]
    public async Task AnOldFileAMoveCutShortLeftInPlaceMovesAtTheNextStart()
    {
        CopyFixtures();
        Open().Dispose();
        SqliteConnection.ClearAllPools();
        File.Move(Path.Combine(Migrated, "sync-history.json"), Path.Combine(Folder, "sync-history.json"));
        Logger.Levels.Clear();

        var database = Open();

        Assert.Equal(2, await CountAsync(database, "sync_runs"));
        Assert.False(File.Exists(Path.Combine(Folder, "sync-history.json")));
        Assert.True(File.Exists(Path.Combine(Migrated, "sync-history.json")));
        Assert.DoesNotContain(LogLevel.Warning, Logger.Levels);
    }

    // Once the folder is erased, files that come back are left alone instead of filling a folder the next night would erase.
    [Fact]
    public async Task OldFilesThatComeBackAfterTheErasureStayWhereTheyAre()
    {
        CopyFixtures();
        var database = Open();
        _clock.Now += TimeSpan.FromDays(31);
        Assert.True(await database.DeleteExpiredMigratedAsync(CancellationToken.None));
        database.Dispose();
        SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(Path.Combine(Folder, "sync-history.json"), """{"Version":1,"Runs":[]}""");

        Open();

        Assert.True(File.Exists(Path.Combine(Folder, "sync-history.json")));
        Assert.False(Directory.Exists(Migrated));
    }

    // A stub file that parses but holds a null entry or path counts as damaged, so the import completes with its warning.
    [Theory]
    [InlineData("""{"Version":2,"Stubs":[null],"Series":[]}""")]
    [InlineData("""{"Version":2,"Stubs":[{"Id":"a","Kind":"Movie","RelativePath":null,"Sources":[],"IdentityKey":"k"}],"Series":[]}""")]
    [InlineData("""{"Version":2,"Stubs":[{"Id":"a","Kind":"Movie","RelativePath":"a.strm","Sources":null,"IdentityKey":"k"}],"Series":[]}""")]
    [InlineData("""{"Version":2,"Stubs":[{"Id":"a","Kind":"Movie","RelativePath":"a.strm","Sources":[null],"IdentityKey":"k"}],"Series":[]}""")]
    [InlineData("""{"Version":2,"Stubs":[],"Series":[null]}""")]
    [InlineData("""{"Version":2,"Stubs":[],"Series":[{"Id":"s","RelativePath":null,"WorkKey":"w"}]}""")]
    [InlineData("""{"Version":2,"Stubs":[],"Series":[{"Id":"s","RelativePath":"S","WorkKey":"w","Seasons":[null]}]}""")]
    [InlineData("""{"Version":2,"Stubs":[],"Series":[],"Groups":[null]}""")]
    [InlineData("""{"Version":2,"Stubs":[],"Series":[],"Labels":null}""")]
    [InlineData("""{"Version":2,"Stubs":[],"Series":[],"Renames":[null]}""")]
    public async Task AStubFileWithANullEntryCountsAsDamagedAndTheImportCompletes(string json)
    {
        await File.WriteAllTextAsync(Path.Combine(Folder, "federated-stubs.json"), json);

        var database = Open();

        Assert.Equal(StoreState.Healthy, database.Health.State);
        Assert.Equal(0, await CountAsync(database, "stub_entries"));
        Assert.Contains(Logger.Lines, line => line.Contains("federated-stubs.json", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(Migrated, "federated-stubs.json")));
    }

    // A folder for the old files that is a link is never filled, made private or erased, so nothing outside it changes.
    [Fact]
    public async Task AFolderForTheOldFilesThatIsALinkIsNeitherFilledNorErased()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var outside = Path.Combine(Root, "outside");
        Directory.CreateDirectory(outside);
        File.SetUnixFileMode(outside, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        await File.WriteAllTextAsync(Path.Combine(outside, "federated-stubs.json"), "outside");
        Directory.CreateSymbolicLink(Migrated, outside);
        CopyFixtures();

        var database = Open();
        _clock.Now += TimeSpan.FromDays(31);
        Assert.False(await database.DeleteExpiredMigratedAsync(CancellationToken.None));

        Assert.Equal(StoreState.Healthy, database.Health.State);
        Assert.Equal([Path.Combine(outside, "federated-stubs.json")], Directory.EnumerateFiles(outside));
        Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(outside, "federated-stubs.json")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute, File.GetUnixFileMode(outside));
        Assert.True(File.Exists(Path.Combine(Folder, "federated-stubs.json")));
        Assert.True(File.Exists(ConfigurationFile));
        Assert.Contains(LogLevel.Warning, Logger.Levels);
    }

    // The erasure never follows a link inside the folder: the link goes, what it points to stays as it was.
    [Fact]
    public async Task TheErasureRemovesLinksInTheFolderWithoutTouchingWhatTheyPointTo()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        CopyFixtures();
        var database = Open();
        var outside = Path.Combine(Root, "outside");
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(Root, "outside.txt");
        await File.WriteAllTextAsync(outsideFile, "outside");
        await File.WriteAllTextAsync(Path.Combine(outside, "inner.txt"), "inner");
        File.CreateSymbolicLink(Path.Combine(Migrated, "linked-file"), outsideFile);
        Directory.CreateSymbolicLink(Path.Combine(Migrated, "linked-folder"), outside);
        _clock.Now += TimeSpan.FromDays(31);

        Assert.True(await database.DeleteExpiredMigratedAsync(CancellationToken.None));

        Assert.False(Directory.Exists(Migrated));
        Assert.Equal("outside", await File.ReadAllTextAsync(outsideFile));
        Assert.Equal("inner", await File.ReadAllTextAsync(Path.Combine(outside, "inner.txt")));
    }

    [Fact]
    public void ANewInstallHasNothingToMoveAndNoFolderForIt()
    {
        var database = Open();

        Assert.Equal(StoreState.Healthy, database.Health.State);
        Assert.False(Directory.Exists(Migrated));
    }

    // D25: the old files stay 30 days, then are overwritten and deleted if the database passes its check.
    [Fact]
    public async Task TheOldFilesAreErasedOnlyThirtyDaysAfterTheMigration()
    {
        CopyFixtures();
        var database = Open();

        _clock.Now += TimeSpan.FromDays(30) - TimeSpan.FromMinutes(1);
        Assert.False(await database.DeleteExpiredMigratedAsync(CancellationToken.None));
        Assert.True(Directory.Exists(Migrated));

        _clock.Now += TimeSpan.FromMinutes(1);
        Assert.True(await database.DeleteExpiredMigratedAsync(CancellationToken.None));
        Assert.False(Directory.Exists(Migrated));
    }

    // A database that fails its check may need the old files to start again, so they stay until a night it passes.
    [Fact]
    public async Task TheOldFilesStayWhileTheDatabaseFailsItsCheckAndGoTheNextNightItPasses()
    {
        CopyFixtures();
        var database = Open();
        _clock.Now += TimeSpan.FromDays(31);
        QuickCheckResult = "*** in database main *** Page 7: btreeInitPage() returns error code 11";

        Assert.False(await database.DeleteExpiredMigratedAsync(CancellationToken.None));
        Assert.Equal(4, Directory.EnumerateFiles(Migrated).Count());
        Assert.Contains(LogLevel.Warning, Logger.Levels);

        QuickCheckResult = "ok";
        Assert.True(await database.DeleteExpiredMigratedAsync(CancellationToken.None));
        Assert.False(Directory.Exists(Migrated));
    }

    // A file that cannot be overwritten is not deleted unread: the others go, it stays with a warning, and the next night tries again.
    [Fact]
    public async Task AnOldFileThatCannotBeOverwrittenStaysUntilTheNextNightWhileTheOthersGo()
    {
        CopyFixtures();
        var database = Open();
        _clock.Now += TimeSpan.FromDays(31);
        var held = Path.Combine(Migrated, "federated-stubs.json");

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(await database.DeleteExpiredMigratedAsync(CancellationToken.None));
        }

        Assert.Equal([held], Directory.EnumerateFiles(Migrated));
        Assert.Contains(LogLevel.Warning, Logger.Levels);

        Assert.True(await database.DeleteExpiredMigratedAsync(CancellationToken.None));
        Assert.False(Directory.Exists(Migrated));
    }

    // The key move of JELLYMESH_SECRET_KEY reads the imported servers, so it runs on the database after the import.
    [Fact]
    public void CredentialsOfAnOlderVariableMoveToTheKeyFileAfterTheImport()
    {
        var id = Guid.NewGuid();
        var oldValue = new CredentialVault(new FixedKeySource(TestValues.Key), new InMemoryConfigurationStore()).Protect(id, TestValues.Credentials)!;
        var config = new PluginConfiguration();
        config.Servers.Add(new RemoteServerConfig { Id = id, Name = "Old", Url = "http://remote:8096", EncryptedCredentials = oldValue });
        using (var stream = File.Create(ConfigurationFile))
        {
            new XmlSerializer(typeof(PluginConfiguration)).Serialize(stream, config);
        }

        var database = Open();
        var store = new PluginConfigurationStore(database, new FixedFederatedRootSource(null), new CapturingLogger<PluginConfigurationStore>(), _clock);
        var vault = new CredentialVault(new FixedKeySource(TestValues.OtherKey), store, new FixedLegacyKeySource(TestValues.Key));

        var moved = store.Read(read => read.Servers.Single().EncryptedCredentials);
        Assert.NotEqual(oldValue, moved);
        Assert.Equal(TestValues.Password, vault.Unprotect(id, moved)!.Password);
    }
}
