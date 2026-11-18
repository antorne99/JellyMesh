using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// <see cref="IPluginDatabase"/> over <c>jellymesh.db</c> in the plugin data folder, through the Microsoft.Data.Sqlite that
/// Jellyfin itself loads (row 421). It opens on first use and never throws from opening: a failure becomes its health.
/// </summary>
public sealed class PluginDatabase : IPluginDatabase, IDisposable
{
    /// <summary>The file name.</summary>
    public const string FileName = "jellymesh.db";

    // The wait on another connection's lock, in seconds (row 428); PRAGMA busy_timeout does not drive it (F23).
    private const int BusyTimeoutSeconds = 5;

    // SQLITE_CORRUPT and SQLITE_NOTADB: the file is not a database SQLite can read.
    private const int Corrupt = 11;
    private const int NotADatabase = 26;

    private static readonly string[] _walFiles = ["-wal", "-shm"];

    // What goes aside with a damaged file: its logs belong to it, and SQLite would read them into the file taking its place.
    private static readonly string[] _logFiles = ["-wal", "-journal"];

    private readonly string _folder;
    private readonly string _path;
    private readonly string _connectionString;
    private readonly string _readOnlyConnectionString;
    private readonly ILogger<PluginDatabase> _logger;
    private readonly IKeyFileGuard _guard;
    private readonly IReadOnlyList<SchemaMigration> _migrations;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _writer;
    private StoreHealth? _health;
    private StoreRecovery? _recovery;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginDatabase"/> class.
    /// </summary>
    /// <param name="folder">The plugin data folder.</param>
    /// <param name="logger">The logger.</param>
    public PluginDatabase(string folder, ILogger<PluginDatabase> logger)
        : this(folder, logger, KeyFileGuards.ForThisSystem(), SchemaMigrations.All, BusyTimeoutSeconds)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginDatabase"/> class with its own guard, schema steps and lock wait.
    /// </summary>
    /// <param name="folder">The plugin data folder.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="guard">Keeps the files private.</param>
    /// <param name="migrations">The schema steps, with versions 1, 2, 3 and so on.</param>
    /// <param name="busyTimeoutSeconds">How long a connection waits on another one's lock.</param>
    /// <param name="time">The clock that dates copies and damaged files; the system clock when <c>null</c>.</param>
    internal PluginDatabase(string folder, ILogger<PluginDatabase> logger, IKeyFileGuard guard, IReadOnlyList<SchemaMigration> migrations, int busyTimeoutSeconds, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        for (var index = 0; index < migrations.Count; index++)
        {
            if (migrations[index].Version != index + 1)
            {
                throw new ArgumentException("The schema steps must have versions 1, 2, 3 and so on, in order.", nameof(migrations));
            }
        }

        _folder = folder;
        _path = Path.Combine(folder, FileName);
        _logger = logger;
        _guard = guard;
        _migrations = migrations;
        _time = time ?? TimeProvider.System;

        // ReadWrite, not ReadWriteCreate: the store makes a missing file private itself, and never replaces one that does not open.
        var settings = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = busyTimeoutSeconds
        };
        _connectionString = settings.ToString();

        // The check before opening for writes: a read-only connection never moves a pending log into the file on close.
        settings.Mode = SqliteOpenMode.ReadOnly;
        _readOnlyConnectionString = settings.ToString();
    }

    /// <summary>
    /// Gets the path of the database file.
    /// </summary>
    public string FilePath => _path;

    /// <inheritdoc />
    public StoreHealth Health => Volatile.Read(ref _health) ?? OpenFirst();

    /// <inheritdoc />
    public StoreRecovery? Recovery
    {
        get
        {
            _ = Health;
            return Volatile.Read(ref _recovery);
        }
    }

    private string RecoveryPath => Path.Combine(_folder, StoreBackups.RecoveryFileName);

    private int Latest => _migrations.Count;

    /// <inheritdoc />
    public StoreHealth Retry()
    {
        _gate.Wait();
        try
        {
            if (_health is { Suspended: false } healthy)
            {
                return healthy;
            }

            return SetHealth(Open());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void DismissRecovery()
    {
        _gate.Wait();
        try
        {
            File.Delete(RecoveryPath);
            Volatile.Write(ref _recovery, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task<T> ReadAsync<T>(Func<SqliteConnection, SqliteTransaction, T> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        return Task.Run(
            () =>
            {
                ThrowIfSuspended(Health);
                using var connection = Connect(_connectionString, queryOnly: true);
                using var transaction = connection.BeginTransaction(deferred: true);
                return read(connection, transaction);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Write(write);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task WriteAsync(Action<SqliteConnection, SqliteTransaction> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        return WriteAsync(
            (connection, transaction) =>
            {
                write(connection, transaction);
                return 0;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> TruncateWalAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return TruncateWal();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<StoreBackup> BackUpAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return BackUp();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
        _gate.Dispose();
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every command text is a constant of this class or a pragma with a version number; no outside value goes in.")]
    private static SqliteCommand Command(SqliteConnection connection, string text, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        return command;
    }

    private static object? Scalar(SqliteConnection connection, string text, SqliteTransaction? transaction = null)
    {
        using var command = Command(connection, text, transaction);
        return command.ExecuteScalar();
    }

    private static void ThrowIfSuspended(StoreHealth health)
    {
        if (health.Suspended)
        {
            throw new StoreSuspendedException("The JellyMesh database is suspended: " + health.State + ".");
        }
    }

    private StoreHealth SetHealth(StoreHealth health)
    {
        Volatile.Write(ref _health, health);
        return health;
    }

    private StoreHealth OpenFirst()
    {
        _gate.Wait();
        try
        {
            return _health ?? SetHealth(Open());
        }
        finally
        {
            _gate.Release();
        }
    }

    // Microsoft.Data.Sqlite does its work synchronously; these run with the gate held.
    private T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write)
    {
        var writer = Writer();
        using var transaction = writer.BeginTransaction(deferred: false);
        var result = write(writer, transaction);
        transaction.Commit();
        return result;
    }

    private bool TruncateWal()
    {
        using var command = Command(Writer(), "PRAGMA wal_checkpoint(TRUNCATE);");
        using var reader = command.ExecuteReader();

        // The first column is 1 when a reader kept the checkpoint from finishing.
        return reader.Read() && reader.GetInt64(0) == 0;
    }

    // VACUUM INTO makes its file 0644 and refuses one that is not empty (F23), so it fills an empty private file. It runs
    // outside any transaction, on the writer with the gate held; a failed copy is deleted, and only then are old ones pruned.
    private StoreBackup BackUp()
    {
        var writer = Writer();
        var now = _time.GetUtcNow();
        var copy = new StoreBackup(StoreBackups.BackupPath(_folder, now), DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()));
        File.Delete(copy.FilePath);
        _guard.CreatePrivate(copy.FilePath).Dispose();
        try
        {
            using var command = Command(writer, "VACUUM INTO $path;");
            command.Parameters.AddWithValue("$path", copy.FilePath);
            command.ExecuteNonQuery();
        }
        catch
        {
            File.Delete(copy.FilePath);
            throw;
        }

        StoreBackups.Prune(_folder);
        _logger.LogInformation("The JellyMesh database was copied to {Copy}", Path.GetFileName(copy.FilePath));
        return copy;
    }

    // The writer of a healthy store, opening it first if nothing has; called with the gate held.
    private SqliteConnection Writer()
    {
        var health = _health ?? SetHealth(Open());
        ThrowIfSuspended(health);
        return _writer!;
    }

    // A connection with the settings of row 428; secure_delete is per connection, so it is set on each (F23).
    private static SqliteConnection Connect(string connectionString, bool queryOnly = false)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            using var pragmas = Command(connection, queryOnly ? "PRAGMA secure_delete = ON; PRAGMA query_only = ON;" : "PRAGMA secure_delete = ON;");
            pragmas.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // Permissions, then the file, which is set aside and replaced when damaged; called with the gate held.
    private StoreHealth Open()
    {
        _writer?.Dispose();
        _writer = null;

        try
        {
            KeepFilesPrivate();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unavailable("The database files cannot be kept private", exception);
        }

        Volatile.Write(ref _recovery, ReadRecovery());
        var health = OpenFile();
        return health.State == StoreState.Damaged ? Recover(health) : health;
    }

    // Version and integrity read-only, then WAL and the schema steps on the writer. The file is opened for writes only once
    // its version is known and it passes the check. Every connection is closed when it returns.
    private StoreHealth OpenFile()
    {
        SqliteConnection? writer = null;
        try
        {
            int version;
            using (var probe = Connect(_readOnlyConnectionString))
            {
                version = Convert.ToInt32(Scalar(probe, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
                if (version > Latest)
                {
                    _logger.LogWarning("The JellyMesh database has schema version {Version}, newer than {Latest} of this build. It is left as it is", version, Latest);
                    return new StoreHealth(StoreState.NewerSchema, $"The database has schema version {version}; this build knows up to {Latest}.");
                }

                var check = Convert.ToString(Scalar(probe, "PRAGMA quick_check;"), CultureInfo.InvariantCulture);
                if (!string.Equals(check, "ok", StringComparison.Ordinal))
                {
                    _logger.LogWarning("The JellyMesh database failed its integrity check");
                    return new StoreHealth(StoreState.Damaged, "The database failed its integrity check.");
                }
            }

            writer = Connect(_connectionString);
            var mode = Convert.ToString(Scalar(writer, "PRAGMA journal_mode = WAL;"), CultureInfo.InvariantCulture);
            if (!string.Equals(mode, "wal", StringComparison.Ordinal))
            {
                return Unavailable("The database cannot use write-ahead logging", null);
            }

            Migrate(writer, version);

            // A read in WAL mode makes the log files when nothing was written. SQLite gives them the mode of the database on
            // Unix and the folder's access list on Windows, so they are narrowed here, before the store is healthy.
            Scalar(writer, "SELECT count(*) FROM sqlite_master;");
            foreach (var suffix in _walFiles)
            {
                _guard.KeepPrivate(_path + suffix);
            }

            _writer = writer;
            writer = null;
            return StoreHealth.Healthy;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is Corrupt or NotADatabase)
        {
            _logger.LogWarning(exception, "SQLite reads the JellyMesh database as damaged");
            return new StoreHealth(StoreState.Damaged, "The database is damaged: " + exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unavailable("The database cannot be opened", exception);
        }
        finally
        {
            writer?.Dispose();
        }
    }

    // Sets the damaged file aside with its logs, then restarts from the newest copy that opens, or empty (row 427). A copy
    // that is damaged too is passed over. When the file cannot be set aside nothing new is made and the store stays damaged.
    private StoreHealth Recover(StoreHealth damage)
    {
        var now = _time.GetUtcNow();
        string damaged;
        try
        {
            damaged = SetAside(now);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The damaged JellyMesh database cannot be set aside. Sync and remote playback stay suspended");
            return damage with { Reason = damage.Reason + " It cannot be set aside: " + exception.Message };
        }

        try
        {
            foreach (var copy in StoreBackups.List(_folder))
            {
                Restore(copy.FilePath);
                var health = OpenFile();
                if (health.State == StoreState.Damaged)
                {
                    _logger.LogWarning("The JellyMesh database copy {Copy} is damaged too", Path.GetFileName(copy.FilePath));
                    DeleteDatabaseFiles();
                    continue;
                }

                _logger.LogWarning("The damaged JellyMesh database was set aside as {File} and restarted from the copy of {Date}. Check the server list", Path.GetFileName(damaged), copy.CreatedAt);
                Remember(new StoreRecovery(now, Path.GetFileName(damaged), copy.CreatedAt));
                return health;
            }

            _guard.CreatePrivate(_path).Dispose();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DeleteDatabaseFiles();
            return Unavailable("The damaged database was set aside, and no copy or empty database can take its place", exception);
        }

        _logger.LogWarning("The damaged JellyMesh database was set aside as {File} and, with no copy, restarted empty", Path.GetFileName(damaged));
        Remember(new StoreRecovery(now, Path.GetFileName(damaged), null));
        return OpenFile();
    }

    // Moves the file and its logs to a free .damaged-<date> name; the shared-memory index is rebuilt by SQLite, so it goes.
    private string SetAside(DateTimeOffset now)
    {
        var target = StoreBackups.DamagedPath(_folder, now);
        File.Move(_path, target);
        foreach (var suffix in _logFiles)
        {
            if (File.Exists(_path + suffix))
            {
                File.Move(_path + suffix, target + suffix);
            }
        }

        File.Delete(_path + "-shm");
        return target;
    }

    // Copies a nightly copy into a new private database file, never moving the copy itself.
    private void Restore(string copy)
    {
        using var source = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var target = _guard.CreatePrivate(_path);
        source.CopyTo(target);
        target.Flush(true);
    }

    private void DeleteDatabaseFiles()
    {
        File.Delete(_path);
        foreach (var suffix in _walFiles)
        {
            File.Delete(_path + suffix);
        }
    }

    // Keeps the recovery for the page, in a private file so it outlives a restart; when the file cannot be written the
    // page still has it until the next restart.
    private void Remember(StoreRecovery recovery)
    {
        Volatile.Write(ref _recovery, recovery);
        try
        {
            File.Delete(RecoveryPath);
            using var stream = _guard.CreatePrivate(RecoveryPath);
            JsonSerializer.Serialize(stream, recovery);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The JellyMesh database recovery cannot be kept for after a restart");
        }
    }

    private StoreRecovery? ReadRecovery()
    {
        try
        {
            if (!File.Exists(RecoveryPath))
            {
                return null;
            }

            using var stream = new FileStream(RecoveryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<StoreRecovery>(stream);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The JellyMesh database recovery note cannot be read and is ignored");
            return null;
        }
    }

    private StoreHealth Unavailable(string reason, Exception? exception)
    {
        _logger.LogWarning(exception, "{Reason}. Sync and remote playback stay suspended", reason);
        return new StoreHealth(StoreState.Unavailable, exception is null ? reason + "." : reason + ": " + exception.Message);
    }

    // A missing database is made empty and private, as SQLite reads an empty file as a new database. An existing one, its
    // side files and every copy next to it become private (rows 417, 428).
    private void KeepFilesPrivate()
    {
        Directory.CreateDirectory(_folder);
        if (!File.Exists(_path))
        {
            _guard.CreatePrivate(_path).Dispose();
        }

        foreach (var file in Directory.EnumerateFiles(_folder, FileName + "*"))
        {
            _guard.KeepPrivate(file);
        }
    }

    // Each step in its own IMMEDIATE transaction with its version, so a failed step leaves the one before.
    private void Migrate(SqliteConnection writer, int version)
    {
        foreach (var migration in _migrations)
        {
            if (migration.Version <= version)
            {
                continue;
            }

            using var transaction = writer.BeginTransaction(deferred: false);
            migration.Apply(writer, transaction);
            using var stamp = Command(writer, "PRAGMA user_version = " + migration.Version.ToString(CultureInfo.InvariantCulture) + ";", transaction);
            stamp.ExecuteNonQuery();
            transaction.Commit();
            _logger.LogInformation("The JellyMesh database moved to schema version {Version}", migration.Version);
        }
    }
}
