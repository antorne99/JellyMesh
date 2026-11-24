using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
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

    // SQLITE_CONSTRAINT, as from a unique index rebuilt over equal rows.
    private const int Constraint = 19;

    private static readonly string[] _walFiles = ["-wal", "-shm"];

    // What goes aside with a damaged file: its logs belong to it, and SQLite would read them into the file taking its place.
    private static readonly string[] _logFiles = ["-wal", "-journal"];

    private static readonly string[] _sideFiles = ["-wal", "-shm", "-journal"];

    private readonly string _folder;
    private readonly string _path;
    private readonly string _connectionString;
    private readonly string _readOnlyConnectionString;
    private readonly ILogger<PluginDatabase> _logger;
    private readonly IKeyFileGuard _guard;
    private readonly IReadOnlyList<SchemaMigration> _migrations;
    private readonly TimeProvider _time;
    private readonly LegacyImport? _legacy;
    private static readonly TimeSpan _disposeWait = TimeSpan.FromSeconds(10);
    private static readonly StoreHealth _closed = new(StoreState.Unavailable, "The database is closed.");
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "A call after Dispose still waits on the gate and is refused as closed; the semaphore never makes a wait handle.")]
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Guards the health and the fields below it; held for an instant, never around SQLite, a file or the gate.
    private readonly Lock _access = new();
    private volatile bool _disposed;
    private long _generation;
    private SqliteConnection? _writer;
    private StoreHealth? _health;

    // The damage an access found, until a recovery replaces the file; the recovery waits for the reads still in progress.
    private StoreHealth? _damage;
    private bool _recoveryWaits;
    private int _readers;
    private RecoveryNote? _note;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginDatabase"/> class.
    /// </summary>
    /// <param name="folder">The plugin data folder.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="legacy">The migration of the 0.4 and 0.5 data, which runs at the first healthy open; none when null.</param>
    public PluginDatabase(string folder, ILogger<PluginDatabase> logger, LegacyImport? legacy = null)
        : this(folder, logger, KeyFileGuards.ForThisSystem(), SchemaMigrations.All, BusyTimeoutSeconds, legacy: legacy)
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
    /// <param name="legacy">The migration of the 0.4 and 0.5 data, which runs at the first healthy open; none when null.</param>
    internal PluginDatabase(string folder, ILogger<PluginDatabase> logger, IKeyFileGuard guard, IReadOnlyList<SchemaMigration> migrations, int busyTimeoutSeconds, TimeProvider? time = null, LegacyImport? legacy = null)
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
        _legacy = legacy;

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

            // A restore not ended yet has no copy date to show.
            return Volatile.Read(ref _note) is { Restoring: false } note ? note.Recovery : null;
        }
    }

    private string RecoveryPath => Path.Combine(_folder, StoreBackups.RecoveryFileName);

    private string HeldPath => Path.Combine(_folder, StoreBackups.HeldFileName);

    private string RecoveriesPath => Path.Combine(_folder, StoreBackups.RecoveriesFileName);

    private int Latest => _migrations.Count;

    /// <inheritdoc />
    public long Generation => Interlocked.Read(ref _generation);

    /// <inheritdoc />
    public StoreHealth Retry()
    {
        _gate.Wait();
        try
        {
            lock (_access)
            {
                if (_health is { } health && (!health.Suspended || _recoveryWaits))
                {
                    return health;
                }
            }

            return Reopen(retried: true);
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
            // A recovery cut short keeps its note, which the next open needs to go on.
            if (_note is { Restoring: true })
            {
                return;
            }

            File.Delete(RecoveryPath);
            Volatile.Write(ref _note, null);
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
        return Task.Run(() => Read(read), cancellationToken);
    }

    /// <inheritdoc />
    public Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        return WithGateAsync(() => Write(write), cancellationToken);
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
    public Task<bool> TruncateWalAsync(CancellationToken cancellationToken) => WithGateAsync(TruncateWal, cancellationToken);

    /// <inheritdoc />
    public Task<StoreBackup> BackUpAsync(CancellationToken cancellationToken) => WithGateAsync(BackUp, cancellationToken);

    /// <inheritdoc />
    public Task DeleteExpiredDamagedAsync(CancellationToken cancellationToken) => WithGateAsync(
        () =>
        {
            _ = Writer();
            DeleteExpiredDamaged();
            PruneDamaged();
            return true;
        },
        cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeleteExpiredMigratedAsync(CancellationToken cancellationToken) => WithGateAsync(
        () =>
        {
            var writer = Writer();
            return _legacy?.DeleteExpired(writer, _time.GetUtcNow()) ?? true;
        },
        cancellationToken);

    /// <inheritdoc />
    public Task<DateTimeOffset?> MigratedKeptUntilAsync(CancellationToken cancellationToken) => WithGateAsync(
        () =>
        {
            var writer = Writer();
            return _legacy?.KeptUntil(writer);
        },
        cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        // Waits a bounded time for a write in progress, so its transaction ends before the writer closes.
        var held = _gate.Wait(_disposeWait);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SetHealth(_closed);
            if (held)
            {
                _writer?.Dispose();
                _writer = null;
            }
            else
            {
                _logger.LogWarning("A JellyMesh database write did not end in time, so its connection is left open");
            }
        }
        finally
        {
            if (held)
            {
                _gate.Release();
            }
        }
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

    // The full check also compares the indexes with their tables, which the quick one leaves out.
    private static bool PassesCheck(SqliteConnection connection, bool full, SqliteTransaction? transaction = null) =>
        string.Equals(Convert.ToString(Scalar(connection, full ? "PRAGMA integrity_check;" : "PRAGMA quick_check;", transaction), CultureInfo.InvariantCulture), "ok", StringComparison.Ordinal);

    // The full check. A file it fails is checked again with every index rebuilt, in one transaction kept only if it passes,
    // so a rebuild that mends nothing leaves the file as it was (row 523). Reads go on meanwhile, and writes wait.
    private bool PassesFullCheck(SqliteConnection connection)
    {
        try
        {
            if (PassesCheck(connection, full: true))
            {
                return true;
            }

            using var transaction = connection.BeginTransaction(deferred: false);
            Scalar(connection, "REINDEX;", transaction);
            if (!PassesCheck(connection, full: true, transaction))
            {
                return false;
            }

            transaction.Commit();
        }
        catch (SqliteException exception) when (IsDamage(exception) || exception.SqliteErrorCode == Constraint)
        {
            return false;
        }

        _logger.LogWarning("The indexes of {File} did not match their tables and were rebuilt", Path.GetFileName(connection.DataSource));
        return true;
    }

    // The full check of a file nothing else reads. A copy can be made again, so the journal of its rebuild stays in memory.
    private bool FilePassesFullCheck(string path, bool isCopy = true)
    {
        try
        {
            using var connection = Connect(new SqliteConnectionStringBuilder(_connectionString) { DataSource = path }.ToString());
            if (isCopy)
            {
                Scalar(connection, "PRAGMA journal_mode = MEMORY;");
            }

            return PassesFullCheck(connection);
        }
        catch (SqliteException exception) when (IsDamage(exception))
        {
            return false;
        }
    }

    private static void ThrowIfSuspended(StoreHealth health)
    {
        if (health.Suspended)
        {
            throw new StoreSuspendedException("The JellyMesh database is suspended: " + health.State + ".");
        }
    }

    // Each healthy open, the first one included, counts as a new generation. A closed store stays closed.
    private StoreHealth SetHealth(StoreHealth health)
    {
        lock (_access)
        {
            health = _disposed ? _closed : health;
            if (!health.Suspended)
            {
                Interlocked.Increment(ref _generation);
            }

            if (!health.Suspended)
            {
                _damage = null;
            }

            Volatile.Write(ref _health, health);
            return health;
        }
    }

    private StoreHealth OpenFirst()
    {
        _gate.Wait();
        try
        {
            return _health ?? Reopen();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Opens the file and takes its health, leaving no writer behind a store closed meanwhile; called with the gate held.
    private StoreHealth Reopen(bool retried = false)
    {
        var health = SetHealth(Open(retried));
        if (_disposed)
        {
            _writer?.Dispose();
            _writer = null;
        }

        return health;
    }

    private static bool IsDamage(SqliteException exception) => exception.SqliteErrorCode is Corrupt or NotADatabase;

    // Suspends the store for damage an access found, so that one recovery sets the file aside and restarts it (row 512).
    private StoreSuspendedException MarkDamaged(SqliteException exception)
    {
        bool marked;
        lock (_access)
        {
            marked = _health is { Suspended: false };
            if (marked)
            {
                _damage = new StoreHealth(StoreState.Damaged, "The database is damaged: " + ReasonOf(exception));
                _recoveryWaits = true;
                Volatile.Write(ref _health, _damage);
            }
        }

        if (marked)
        {
            _logger.LogWarning(exception, "SQLite reads the JellyMesh database as damaged");
            StartRecovery();
        }

        return new StoreSuspendedException("The JellyMesh database is damaged.", exception);
    }

    // Starts the recovery that waits once no read is in progress: none sees the file change under it, or its logs go with it.
    private void StartRecovery()
    {
        lock (_access)
        {
            if (!_recoveryWaits || _readers > 0)
            {
                return;
            }

            _recoveryWaits = false;
        }

        _ = Task.Run(RecoverDamaged);
    }

    private void RecoverDamaged()
    {
        _gate.Wait();
        try
        {
            // A retry or the closing of the store may have come first.
            if (Volatile.Read(ref _damage) is not null && !_disposed)
            {
                Reopen();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The damaged JellyMesh database cannot be replaced yet. Sync and remote playback stay suspended");
        }
        finally
        {
            _gate.Release();
        }
    }

    // Runs on the one writer. Damage it meets suspends the store; every other error goes to the caller as it is.
    private async Task<T> WithGateAsync<T>(Func<T> access, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return access();
        }
        catch (SqliteException exception) when (IsDamage(exception))
        {
            throw MarkDamaged(exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    // A read counts until its connection is closed, so a recovery does not start before it ends.
    private T Read<T>(Func<SqliteConnection, SqliteTransaction, T> read)
    {
        _ = Health;
        lock (_access)
        {
            ThrowIfSuspended(_health!);
            _readers++;
        }

        try
        {
            using var connection = Connect(_connectionString, queryOnly: true);
            using var transaction = connection.BeginTransaction(deferred: true);
            return read(connection, transaction);
        }
        catch (SqliteException exception) when (IsDamage(exception))
        {
            throw MarkDamaged(exception);
        }
        finally
        {
            lock (_access)
            {
                _readers--;
            }

            StartRecovery();
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

    // Empties a log a crash left behind, which may hold replaced credentials; a busy or failed checkpoint never stops the open.
    private void EmptyLogAtOpen(SqliteConnection writer)
    {
        try
        {
            using var command = Command(writer, "PRAGMA wal_checkpoint(TRUNCATE);");
            using var reader = command.ExecuteReader();
            if (reader.Read() && reader.GetInt64(0) != 0)
            {
                _logger.LogDebug("The JellyMesh database log was busy at open; it is emptied at the next credential change or nightly copy");
            }
        }
        catch (SqliteException exception) when (!IsDamage(exception))
        {
            _logger.LogWarning("The JellyMesh database log could not be emptied at open: {Error}", exception.Message);
        }
    }

    // VACUUM INTO makes its file 0644 and refuses one that is not empty (F23), so it fills an empty private file, under a
    // name a recovery never takes until it is complete. It runs outside any transaction, on the writer with the gate held.
    // The database is checked in full first, and one that fails its check is replaced (row 523).
    private StoreBackup BackUp()
    {
        var writer = Writer();
        if (!PassesFullCheck(writer))
        {
            throw new SqliteException("The database failed its integrity check.", Corrupt);
        }

        var now = _time.GetUtcNow();
        var copy = new StoreBackup(StoreBackups.BackupPath(_folder, now), DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()));
        var partial = StoreBackups.PartialPath(copy.FilePath);
        foreach (var leftover in StoreBackups.Partials(_folder))
        {
            TryErase(leftover, "copy cut short");
        }

        _guard.CreatePrivate(partial).Dispose();
        try
        {
            VacuumInto(writer, partial);
            if (!FilePassesFullCheck(partial))
            {
                throw new InvalidDataException("The copy failed its integrity check, so it is not kept.");
            }

            ThrowIfSuspended(_health!);
            if (File.Exists(copy.FilePath))
            {
                TryErase(copy.FilePath, "copy replaced");
            }

            File.Move(partial, copy.FilePath, overwrite: true);
        }
        catch
        {
            TryErase(partial, "failed copy");
            throw;
        }

        foreach (var pruned in StoreBackups.Pruned(_folder, copy))
        {
            TryErase(pruned, "pruned copy");
        }

        _logger.LogInformation("The JellyMesh database was copied to {Copy}", Path.GetFileName(copy.FilePath));
        return copy;
    }

    // Fills a file with the content of the database. Damage of that file, when the database passes its check, only fails it.
    private void VacuumInto(SqliteConnection writer, string path)
    {
        try
        {
            using var command = Command(writer, "VACUUM INTO $path;");
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }
        catch (SqliteException exception) when (IsDamage(exception))
        {
            if (!PassesFullCheck(writer))
            {
                throw;
            }

            throw new InvalidDataException("The copy is damaged: " + ReasonOf(exception), exception);
        }
    }

    // Overwrites and deletes each damaged file and log once 30 days have passed since the date in its name (D35).
    private void DeleteExpiredDamaged()
    {
        var now = _time.GetUtcNow();
        foreach (var file in StoreBackups.ListDamaged(_folder))
        {
            try
            {
                if (now - DamagedSince(file, now) >= StoreBackups.DamagedKept)
                {
                    StoreBackups.Erase(file.FilePath);
                    _logger.LogInformation("The damaged JellyMesh database file {File} was overwritten and deleted after 30 days", Path.GetFileName(file.FilePath));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("The damaged JellyMesh database file {File} cannot be overwritten and deleted yet: {Error}", Path.GetFileName(file.FilePath), exception.Message);
            }
        }
    }

    // Overwrites and deletes all but the last damaged files set aside (row 525). The one the page still names stays.
    private void PruneDamaged()
    {
        try
        {
            foreach (var file in StoreBackups.DamagedToPrune(_folder, Volatile.Read(ref _note)?.DamagedFile))
            {
                TryErase(file, "damaged file");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The damaged JellyMesh database files cannot be listed: {Error}", exception.Message);
        }
    }

    // The date in the name, or for a date ahead of the clock the file's write time, set to now the first time it is ahead too.
    // A link is never stamped, which would write through it, so with a date ahead it goes at once.
    private static DateTimeOffset DamagedSince(StoreBackup file, DateTimeOffset now)
    {
        if (file.CreatedAt <= now)
        {
            return file.CreatedAt;
        }

        if (new FileInfo(file.FilePath).LinkTarget is not null)
        {
            return DateTimeOffset.MinValue;
        }

        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(file.FilePath), TimeSpan.Zero);
        if (written <= now)
        {
            return written;
        }

        File.SetLastWriteTimeUtc(file.FilePath, now.UtcDateTime);
        return now;
    }

    // Overwrites and deletes a file of the plugin's own; one that cannot go is logged and left for the next night.
    private void TryErase(string path, string what)
    {
        try
        {
            StoreBackups.Erase(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The JellyMesh database {What} {File} cannot be overwritten and deleted yet: {Error}", what, Path.GetFileName(path), exception.Message);
        }
    }

    // The writer of a healthy store, opening it first if nothing has; called with the gate held.
    private SqliteConnection Writer()
    {
        ThrowIfSuspended(_health ?? Reopen());
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

    // Permissions, then the file, which is set aside and replaced when damaged; called with the gate held. A file an access
    // read as damaged is not opened again, since the check at open may not see that damage.
    private StoreHealth Open(bool retried)
    {
        if (_disposed)
        {
            return _closed;
        }

        _writer?.Dispose();
        _writer = null;

        // A recovery cut short after the set-aside goes on, and no empty file takes the place of the copy it was restoring.
        // A note whose damaged file was never set aside, as after a crash between the two, is dropped.
        var note = ReadNote();
        if (note is { Restoring: true } && !File.Exists(Path.Combine(_folder, Path.GetFileName(note.DamagedFile))))
        {
            _logger.LogWarning("The JellyMesh database recovery note names a damaged file that was never set aside and is dropped");
            TryDeleteNote();
            note = null;
        }

        Volatile.Write(ref _note, note);
        try
        {
            KeepFilesPrivate(createMissing: note is not { Restoring: true });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unavailable("The database files cannot be kept private", exception);
        }

        StoreHealth health;
        if (note is { Restoring: true })
        {
            health = Replace(note.RecoveredAt, note.DamagedFile);
        }
        else
        {
            // A store that waits for Retry does so past a restart too, and then its file is checked in full.
            var held = ReadHeld();
            if (held is not null && !retried)
            {
                return held;
            }

            health = Volatile.Read(ref _damage) ?? OpenFile(full: held is not null, isCopy: false);
            health = health.State == StoreState.Damaged ? Recover(health, retried) : health;
        }

        health = ImportLegacy(health, retried, recovered: false);
        if (!health.Suspended)
        {
            TryDelete(HeldPath, "wait for a retry");
        }

        return health;
    }

    // The data of 0.4 and 0.5 goes in before any service sees the store healthy; until it does, the store stays suspended
    // and the next open tries again, with every old file where it was (row 426).
    private StoreHealth ImportLegacy(StoreHealth health, bool retried, bool recovered)
    {
        if (health.Suspended || _legacy is null || _writer is null)
        {
            return health;
        }

        try
        {
            _legacy.Run(_writer, _time.GetUtcNow());
            return health;
        }
        catch (SqliteException exception) when (IsDamage(exception) && !recovered)
        {
            _writer.Dispose();
            _writer = null;
            _logger.LogWarning(exception, "SQLite reads the JellyMesh database as damaged");
            return ImportLegacy(Recover(new StoreHealth(StoreState.Damaged, "The database is damaged: " + ReasonOf(exception)), retried), retried, recovered: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _writer.Dispose();
            _writer = null;
            return Unavailable("The data of the older JellyMesh version cannot be moved into the database yet. Nothing was moved, and the next start tries again", exception);
        }
    }

    // Version and integrity read-only, then WAL and the schema steps on the writer. The file is opened for writes only once
    // its version is known and it passes the check. A full check rebuilds the indexes of a file that fails it.
    private StoreHealth OpenFile(bool full = false, bool isCopy = true)
    {
        SqliteConnection? writer = null;
        try
        {
            int version;
            bool passes;
            using (var probe = Connect(_readOnlyConnectionString))
            {
                version = Convert.ToInt32(Scalar(probe, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
                if (version > Latest)
                {
                    _logger.LogWarning("The JellyMesh database has schema version {Version}, newer than {Latest} of this build. It is left as it is", version, Latest);
                    return new StoreHealth(StoreState.NewerSchema, $"The database has schema version {version}; this build knows up to {Latest}.");
                }

                passes = PassesCheck(probe, full);
            }

            if (!passes && !(full && FilePassesFullCheck(_path, isCopy)))
            {
                _logger.LogWarning("The JellyMesh database failed its integrity check");
                return new StoreHealth(StoreState.Damaged, "The database failed its integrity check.");
            }

            writer = Connect(_connectionString);
            var mode = Convert.ToString(Scalar(writer, "PRAGMA journal_mode = WAL;"), CultureInfo.InvariantCulture);
            if (!string.Equals(mode, "wal", StringComparison.Ordinal))
            {
                return Unavailable("The database cannot use write-ahead logging", null);
            }

            Migrate(writer, version);
            EmptyLogAtOpen(writer);

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
        catch (SqliteException exception) when (IsDamage(exception))
        {
            _logger.LogWarning(exception, "SQLite reads the JellyMesh database as damaged");
            return new StoreHealth(StoreState.Damaged, "The database is damaged: " + ReasonOf(exception));
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

    // Writes the note, then sets the damaged file aside with its logs and replaces it (row 427). The note comes first, so a
    // recovery cut short at any point goes on at the next open; without it, or when the file cannot be set aside, nothing
    // moves, nothing new is made and the store stays damaged. Past the limit of a day it waits for Retry (row 525).
    private StoreHealth Recover(StoreHealth damage, bool retried)
    {
        var now = _time.GetUtcNow();
        IReadOnlyList<DateTimeOffset> earlier = [];
        try
        {
            earlier = RecentRecoveries(now);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The times of the JellyMesh database recoveries cannot be read. Without a retry, sync and remote playback stay suspended");
            if (!retried)
            {
                return Hold(damage with { Reason = damage.Reason + " The times of its recoveries cannot be read: " + ReasonOf(exception) });
            }
        }

        if (!retried && earlier.Count >= StoreBackups.RecoveryLimit)
        {
            _logger.LogWarning("The JellyMesh database was recovered {Count} times in 24 hours, so it stays damaged until a retry. Sync and remote playback stay suspended", earlier.Count);
            return Hold(damage with { Reason = damage.Reason + $" It was recovered {earlier.Count} times in 24 hours, so it stays damaged until a retry." });
        }

        var target = StoreBackups.DamagedPath(_folder, now);
        var damaged = Path.GetFileName(target);
        try
        {
            WriteNote(new RecoveryNote(now, damaged, null, Restoring: true));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The JellyMesh database recovery note cannot be written, so the damaged database is not set aside. Sync and remote playback stay suspended");
            return damage with { Reason = damage.Reason + " The recovery note cannot be written: " + ReasonOf(exception) };
        }

        var failure = " The times of its recoveries cannot be written: ";
        try
        {
            WriteWhole(RecoveriesPath, earlier.TakeLast(StoreBackups.RecoveryLimit - 1).Append(now).ToList());
            failure = " It cannot be set aside: ";
            SetAside(target);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDeleteNote();
            Volatile.Write(ref _note, null);
            _logger.LogWarning(exception, "The damaged JellyMesh database cannot be set aside. Sync and remote playback stay suspended");
            return damage with { Reason = damage.Reason + failure + ReasonOf(exception) };
        }

        Volatile.Write(ref _damage, null);
        return Replace(now, damaged);
    }

    // Restarts from the newest copy that opens, passing over empty and damaged ones, or empty without one.
    private StoreHealth Replace(DateTimeOffset recoveredAt, string damaged)
    {
        try
        {
            // What a replacement cut short may have left.
            DeleteDatabaseFiles();
            foreach (var copy in StoreBackups.ForRestore(_folder, _time.GetUtcNow()))
            {
                if (new FileInfo(copy.FilePath).Length == 0)
                {
                    _logger.LogWarning("The JellyMesh database copy {Copy} is empty and is passed over", Path.GetFileName(copy.FilePath));
                    continue;
                }

                Restore(copy.FilePath);
                var health = OpenFile(full: true);
                if (health.State == StoreState.Unavailable)
                {
                    // The check of the copy may not have ended, so the note stays and the next open restores it again.
                    return health;
                }

                if (health.State == StoreState.Damaged)
                {
                    _logger.LogWarning("The JellyMesh database copy {Copy} is damaged too and is passed over", Path.GetFileName(copy.FilePath));
                    DeleteDatabaseFiles();
                    continue;
                }

                _logger.LogWarning("The damaged JellyMesh database was set aside as {File} and restarted from the copy of {Date}. Check the servers and peers", damaged, copy.CreatedAt);
                return Complete(health, new RecoveryNote(recoveredAt, damaged, copy.CreatedAt));
            }

            _guard.CreatePrivate(_path).Dispose();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDeleteDatabaseFiles();
            return Unavailable("The damaged database was set aside, and no copy or empty database can take its place yet", exception);
        }

        _logger.LogWarning("The damaged JellyMesh database was set aside as {File} and, with no copy, restarted empty. Check the servers and peers", damaged);
        return Complete(OpenFile(), new RecoveryNote(recoveredAt, damaged, null));
    }

    // Ends a recovery by replacing its note in one step. Until then the note says it is restoring and the database is not
    // used, so the next open may replace it again; a note that cannot be replaced keeps the store suspended.
    private StoreHealth Complete(StoreHealth health, RecoveryNote note)
    {
        try
        {
            WriteNote(note);
            if (!health.Suspended)
            {
                PruneDamaged();
            }

            return health;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _writer?.Dispose();
            _writer = null;
            return Unavailable("The database was replaced, but the recovery note cannot be updated", exception);
        }
    }

    // Moves the logs, then the file, to a free .damaged-<date> name, so the path never holds a log without its file; the
    // shared-memory index is rebuilt by SQLite, so it goes. Logs moved for a file that does not move go back.
    private void SetAside(string target)
    {
        var moved = new List<string>();
        try
        {
            foreach (var suffix in _logFiles)
            {
                if (File.Exists(_path + suffix))
                {
                    File.Move(_path + suffix, target + suffix);
                    moved.Add(suffix);
                }
            }

            File.Move(_path, target);
        }
        catch
        {
            foreach (var suffix in moved)
            {
                TryMove(target + suffix, _path + suffix);
            }

            throw;
        }

        File.Delete(_path + "-shm");
    }

    private void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "A log of the damaged JellyMesh database cannot be put back next to it");
        }
    }

    // Copies a nightly copy into a new private database file, never moving the copy itself.
    private void Restore(string copy)
    {
        using var source = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var target = _guard.CreatePrivate(_path);
        source.CopyTo(target);
        target.Flush(true);
    }

    // The database with every side file SQLite reads with it, so none is played into the file that comes next.
    private void DeleteDatabaseFiles()
    {
        File.Delete(_path);
        foreach (var suffix in _sideFiles)
        {
            File.Delete(_path + suffix);
        }
    }

    private void TryDeleteDatabaseFiles()
    {
        try
        {
            DeleteDatabaseFiles();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "A JellyMesh database file left by a recovery cut short cannot be deleted");
        }
    }

    // Keeps the recovery for the page in a private file, so it outlives a restart.
    private void WriteNote(RecoveryNote note)
    {
        WriteWhole(RecoveryPath, note);
        Volatile.Write(ref _note, note);
    }

    // Keeps a store that waits for Retry damaged past a restart too.
    private StoreHealth Hold(StoreHealth damage)
    {
        try
        {
            WriteWhole(HeldPath, damage.Reason);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "That the JellyMesh database waits for a retry cannot be written, so it ends at a restart");
        }

        return damage;
    }

    // The damage of a store that waits for Retry, null for one that does not. One that cannot be read still counts.
    private StoreHealth? ReadHeld()
    {
        if (!File.Exists(HeldPath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(HeldPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new StoreHealth(StoreState.Damaged, JsonSerializer.Deserialize<string>(stream));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Why the JellyMesh database waits for a retry cannot be read. Sync and remote playback stay suspended");
            return new StoreHealth(StoreState.Damaged, "The database was found damaged and stays so until a retry.");
        }
    }

    // The recoveries of the last 24 hours, kept next to the database since they outlive it. A time ahead of the clock, which
    // may have been set back, counts for a day too.
    private List<DateTimeOffset> RecentRecoveries(DateTimeOffset now)
    {
        if (!File.Exists(RecoveriesPath))
        {
            return [];
        }

        using var stream = new FileStream(RecoveriesPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var times = JsonSerializer.Deserialize<List<DateTimeOffset>>(stream) ?? throw new JsonException("The times are missing.");
        return times.Where(time => (now - time).Duration() < StoreBackups.RecoveryWindow).ToList();
    }

    // Writes a private file aside, flushes it and moves it over the old one, so the file on disk is always whole.
    private void WriteWhole<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        try
        {
            File.Delete(temporary);
            using (var stream = _guard.CreatePrivate(temporary))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "A temporary JellyMesh recovery file cannot be deleted");
            }

            throw;
        }
    }

    private void TryDeleteNote() => TryDelete(RecoveryPath, "recovery note");

    private void TryDelete(string path, string what)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The JellyMesh database {What} cannot be deleted", what);
        }
    }

    private RecoveryNote? ReadNote()
    {
        try
        {
            if (!File.Exists(RecoveryPath))
            {
                return null;
            }

            using var stream = new FileStream(RecoveryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<RecoveryNote>(stream);
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
        return new StoreHealth(StoreState.Unavailable, exception is null ? reason + "." : reason + ": " + ReasonOf(exception));
    }

    // The text of a file or SQLite error, which names only files and the schema; any other error, which may carry stored
    // data such as a remote address, only by its kind, since the reason reaches the page.
    internal static string ReasonOf(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SqliteException
            ? LogText.CleanMessage(exception.Message) ?? string.Empty
            : "an unexpected " + exception.GetType().Name + ", described in the Jellyfin log.";

    // A missing database is made empty and private, as SQLite reads an empty file as a new database, unless a recovery is
    // to restore it. An existing one, its side files and every copy next to it become private (rows 417, 428).
    private void KeepFilesPrivate(bool createMissing)
    {
        Directory.CreateDirectory(_folder);
        if (createMissing && !File.Exists(_path))
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

    // The recovery as kept in its file: Restoring while the damaged file is aside and nothing has taken its place yet.
    private sealed record RecoveryNote(DateTimeOffset RecoveredAt, string DamagedFile, DateTimeOffset? CopyDate, bool Restoring = false)
    {
        public StoreRecovery Recovery => new(RecoveredAt, DamagedFile, CopyDate);
    }
}
