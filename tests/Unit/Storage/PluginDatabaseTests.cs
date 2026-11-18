using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Storage;

public sealed class PluginDatabaseTests : IDisposable
{
    private readonly List<PluginDatabase> _opened = [];

    public PluginDatabaseTests() => Directory.CreateDirectory(Folder);

    // Under the temp folder, not the bind mount: WAL needs shared memory a host mount may not give.
    private string Folder { get; } = Path.Combine(Path.GetTempPath(), "jellymesh-store-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(Folder, PluginDatabase.FileName);

    public void Dispose()
    {
        foreach (var database in _opened)
        {
            database.Dispose();
        }

        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, true);
        }
    }

    private PluginDatabase Open(IKeyFileGuard? guard = null, IReadOnlyList<SchemaMigration>? migrations = null, int busyTimeoutSeconds = 1)
    {
        var database = new PluginDatabase(
            Folder,
            new CapturingLogger<PluginDatabase>(),
            guard ?? KeyFileGuards.ForThisSystem(),
            migrations ?? SchemaMigrations.All,
            busyTimeoutSeconds);
        _opened.Add(database);
        return database;
    }

    private static object? Scalar(SqliteConnection connection, string text, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        return command.ExecuteScalar();
    }

    [Fact]
    public async Task ANewFolderOpensHealthyAtTheLatestSchema()
    {
        var database = Open();

        Assert.Equal(StoreState.Healthy, database.Health.State);
        Assert.False(database.Health.Suspended);
        Assert.True(File.Exists(DatabasePath));
        var version = await database.ReadAsync((connection, transaction) => Scalar(connection, "PRAGMA user_version;", transaction), CancellationToken.None);
        Assert.Equal((long)SchemaMigrations.Latest, version);
    }

    [Fact]
    public async Task EveryConnectionIsWalPrivateUnpooledWithSecureDeleteAndTheBusyWait()
    {
        var database = Open();

        void Check(SqliteConnection connection, SqliteTransaction transaction)
        {
            var settings = new SqliteConnectionStringBuilder(connection.ConnectionString);
            Assert.Equal(SqliteCacheMode.Private, settings.Cache);
            Assert.False(settings.Pooling);
            Assert.Equal(1, settings.DefaultTimeout);
            Assert.Equal(SqliteOpenMode.ReadWrite, settings.Mode);
            Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode;", transaction));
            Assert.Equal(1L, Scalar(connection, "PRAGMA secure_delete;", transaction));
        }

        await database.WriteAsync(Check, CancellationToken.None);
        await database.ReadAsync(
            (connection, transaction) =>
            {
                Check(connection, transaction);
                return 0;
            },
            CancellationToken.None);
    }

    // PRAGMA busy_timeout reads 0 there: the wait is the connection string's Default Timeout (F23).
    [Fact]
    public async Task TheServerBuildWaitsFiveSecondsOnALock()
    {
        using var database = new PluginDatabase(Folder, new CapturingLogger<PluginDatabase>());

        var timeout = await database.ReadAsync(
            (connection, _) => new SqliteConnectionStringBuilder(connection.ConnectionString).DefaultTimeout,
            CancellationToken.None);

        Assert.Equal(5, timeout);
    }

    // A file left by a newer build, as after going back to an older one.
    private void WriteDatabase(int version, int rows = 1, string journalMode = "delete")
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        Scalar(connection, $"PRAGMA journal_mode = {journalMode};");
        Scalar(connection, "CREATE TABLE IF NOT EXISTS later (value TEXT); CREATE INDEX IF NOT EXISTS later_value ON later (value);");
        for (var row = 0; row < rows; row++)
        {
            Scalar(connection, $"INSERT INTO later VALUES ('{new string('v', 200)}{row}');");
        }

        Scalar(connection, $"PRAGMA user_version = {version};");
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("wal")]
    public async Task ANewerSchemaLeavesTheFileUntouchedAndSuspends(string journalMode)
    {
        WriteDatabase(SchemaMigrations.Latest + 1, journalMode: journalMode);
        var before = await File.ReadAllBytesAsync(DatabasePath);

        var database = Open();

        Assert.Equal(StoreState.NewerSchema, database.Health.State);
        Assert.True(database.Health.Suspended);
        Assert.Equal(before, await File.ReadAllBytesAsync(DatabasePath));
        Assert.False(File.Exists(DatabasePath + "-wal"));
        Assert.False(File.Exists(DatabasePath + "-shm"));
        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.WriteAsync((_, _) => { }, CancellationToken.None));
        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.ReadAsync((_, _) => 0, CancellationToken.None));
    }

    [Fact]
    public async Task AFileThatIsNotADatabaseIsDamagedAndLeftForTheRecovery()
    {
        var garbage = new byte[8192];
        Array.Fill(garbage, (byte)'x');
        await File.WriteAllBytesAsync(DatabasePath, garbage);

        var database = Open();

        Assert.Equal(StoreState.Damaged, database.Health.State);
        Assert.True(database.Health.Suspended);
        Assert.Equal(garbage, await File.ReadAllBytesAsync(DatabasePath));
    }

    [Fact]
    public async Task AFileThatFailsTheIntegrityCheckIsDamaged()
    {
        WriteDatabase(1, rows: 200);

        // Overwrites the middle of the file, past the header and the schema page.
        var bytes = await File.ReadAllBytesAsync(DatabasePath);
        Array.Fill(bytes, (byte)0x5A, bytes.Length / 2, 4096);
        await File.WriteAllBytesAsync(DatabasePath, bytes);

        var database = Open();

        Assert.Equal(StoreState.Damaged, database.Health.State);
    }

    // As the key file: a database that cannot be kept private is not opened, and nothing new is made.
    [Fact]
    public void AFileThatCannotBeMadePrivateIsUnavailableAndNotCreated()
    {
        var database = Open(new RefusingGuard());

        Assert.Equal(StoreState.Unavailable, database.Health.State);
        Assert.NotNull(database.Health.Reason);
        Assert.False(File.Exists(DatabasePath));
    }

    // The tests run as root, which no file mode stops: a folder in the way stands for a file that does not open.
    [Fact]
    public void AFileThatDoesNotOpenIsUnavailableAndNoNewDatabaseTakesItsPlace()
    {
        Directory.CreateDirectory(DatabasePath);

        var database = Open();

        Assert.Equal(StoreState.Unavailable, database.Health.State);
        Assert.True(Directory.Exists(DatabasePath));
        Assert.Empty(Directory.GetFiles(Folder));
    }

    [Fact]
    public async Task RetryOpensTheDatabaseOnceTheCauseIsGone()
    {
        Directory.CreateDirectory(DatabasePath);
        var database = Open();
        Assert.Equal(StoreState.Unavailable, database.Health.State);

        Directory.Delete(DatabasePath);

        Assert.Equal(StoreState.Healthy, database.Retry().State);
        Assert.Equal(StoreState.Healthy, database.Health.State);
        await database.WriteAsync((_, _) => { }, CancellationToken.None);
    }

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode Shared = OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    [Fact]
    public async Task TheDatabaseAndItsLogFilesAreOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var database = Open();

        // The log files exist only while a connection is open, as the writer is.
        await database.WriteAsync((connection, transaction) => Scalar(connection, "CREATE TABLE secrets (value TEXT);", transaction), CancellationToken.None);
        foreach (var file in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" })
        {
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(file));
        }
    }

    // Files of an earlier build, copies and files set aside are narrowed before SQLite opens anything.
    [Fact]
    public void ExistingFilesAndCopiesAreNarrowedToTheOwner()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        WriteDatabase(1);
        var files = new[] { DatabasePath, DatabasePath + ".damaged-20261118000000", DatabasePath + ".backup-20261118" };
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                File.WriteAllBytes(file, []);
            }

            File.SetUnixFileMode(file, Shared);
        }

        var other = Path.Combine(Folder, "federation-index.json");
        File.WriteAllBytes(other, []);
        File.SetUnixFileMode(other, Shared);

        Assert.Equal(StoreState.Healthy, Open().Health.State);

        foreach (var file in files)
        {
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(file));
        }

        Assert.Equal(Shared, File.GetUnixFileMode(other));
    }

    [Fact]
    public async Task SchemaStepsRunInOrderEachInItsOwnTransaction()
    {
        var seen = new List<long>();
        SchemaMigration Step(int version) => new(
            version,
            (connection, transaction) =>
            {
                seen.Add((long)Scalar(connection, "PRAGMA user_version;", transaction)!);
                Scalar(connection, $"CREATE TABLE step{version} (value TEXT);", transaction);
            });

        var database = Open(migrations: [Step(1), Step(2), Step(3)]);

        Assert.Equal(StoreState.Healthy, database.Health.State);
        Assert.Equal([0L, 1L, 2L], seen);
        Assert.Equal(3L, await database.ReadAsync((connection, transaction) => Scalar(connection, "PRAGMA user_version;", transaction), CancellationToken.None));
    }

    [Fact]
    public async Task AnOpenAtTheCurrentVersionRunsNoStep()
    {
        var first = Open(migrations: [new(1, (_, _) => { })]);
        Assert.Equal(StoreState.Healthy, first.Health.State);
        first.Dispose();

        var ran = false;
        var database = Open(migrations: [new(1, (_, _) => ran = true)]);

        Assert.Equal(StoreState.Healthy, database.Health.State);
        Assert.False(ran);
        await database.WriteAsync((_, _) => { }, CancellationToken.None);
    }

    [Fact]
    public async Task AFailedStepLeavesTheVersionBeforeItAndSuspends()
    {
        var database = Open(migrations:
        [
            new(1, (connection, transaction) => Scalar(connection, "CREATE TABLE first (value TEXT);", transaction)),
            new(2, (connection, transaction) =>
            {
                Scalar(connection, "CREATE TABLE second (value TEXT);", transaction);
                throw new InvalidOperationException("step 2");
            })
        ]);

        Assert.Equal(StoreState.Unavailable, database.Health.State);
        database.Dispose();

        var tables = new List<string>();
        long version;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            version = (long)Scalar(connection, "PRAGMA user_version;")!;
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        Assert.Equal(1L, version);
        Assert.Equal(["first"], tables);
    }

    [Fact]
    public void StepsOutOfOrderAreRefused()
    {
        Assert.Throws<ArgumentException>(() => Open(migrations: [new(2, (_, _) => { }), new(1, (_, _) => { })]));
    }

    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(10);

    private SqliteConnection Outside() =>
        new(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false, DefaultTimeout = 1 }.ToString());

    private static Task<long> CountAsync(PluginDatabase database) =>
        database.ReadAsync((connection, transaction) => (long)Scalar(connection, "SELECT count(*) FROM items;", transaction)!, CancellationToken.None);

    private static async Task<PluginDatabase> WithItemsAsync(PluginDatabase database)
    {
        await database.WriteAsync((connection, transaction) => Scalar(connection, "CREATE TABLE items (value TEXT);", transaction), CancellationToken.None);
        return database;
    }

    private static Task AddItemAsync(PluginDatabase database, string value = "item") => database.WriteAsync(
        (connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO items VALUES ($value);";
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        },
        CancellationToken.None);

    [Fact]
    public async Task AWriteThatThrowsLeavesNothing()
    {
        var database = await WithItemsAsync(Open());

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.WriteAsync(
            (connection, transaction) =>
            {
                Scalar(connection, "INSERT INTO items VALUES ('half');", transaction);
                throw new InvalidOperationException("stopped halfway");
            },
            CancellationToken.None));

        Assert.Equal(0L, await CountAsync(database));
    }

    // IMMEDIATE takes the write lock at the start, so another writer cannot slip in between a read and a write.
    [Fact]
    public async Task AWriteHoldsTheWriteLockFromItsStart()
    {
        var database = await WithItemsAsync(Open());

        var other = await database.WriteAsync(
            (_, _) =>
            {
                using var outside = Outside();
                outside.Open();
                return Assert.Throws<SqliteException>(() => Scalar(outside, "BEGIN IMMEDIATE;")).SqliteErrorCode;
            },
            CancellationToken.None);

        Assert.Equal(5, other);
    }

    [Fact]
    public async Task AWriteWaitsForAnotherWriterAndThenGoesThrough()
    {
        var database = await WithItemsAsync(Open(busyTimeoutSeconds: 5));
        await using var outside = Outside();
        await outside.OpenAsync();
        Scalar(outside, "BEGIN IMMEDIATE;");

        var write = Task.Run(() => AddItemAsync(database));
        await Task.Delay(300);
        Assert.False(write.IsCompleted);
        Scalar(outside, "COMMIT;");

        await write.WaitAsync(_wait);
        Assert.Equal(1L, await CountAsync(database));
    }

    [Fact]
    public async Task TwoWritesOfTheStoreQueue()
    {
        var database = await WithItemsAsync(Open());
        using var firstInside = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var order = new List<string>();

        var first = Task.Run(() => database.WriteAsync(
            (_, _) =>
            {
                firstInside.Set();
                releaseFirst.Wait(_wait);
                order.Add("first");
            },
            CancellationToken.None));
        Assert.True(firstInside.Wait(_wait));
        var second = Task.Run(() => database.WriteAsync((_, _) => order.Add("second"), CancellationToken.None));
        await Task.Delay(200);
        Assert.Empty(order);

        releaseFirst.Set();
        await Task.WhenAll(first, second).WaitAsync(_wait);

        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public async Task AReaderDoesNotBlockTheWriterAndKeepsItsSnapshot()
    {
        var database = await WithItemsAsync(Open());
        await AddItemAsync(database);
        using var readOnce = new ManualResetEventSlim();
        using var written = new ManualResetEventSlim();

        var read = database.ReadAsync(
            (connection, transaction) =>
            {
                var before = (long)Scalar(connection, "SELECT count(*) FROM items;", transaction)!;
                readOnce.Set();
                written.Wait(_wait);
                return (before, after: (long)Scalar(connection, "SELECT count(*) FROM items;", transaction)!);
            },
            CancellationToken.None);
        Assert.True(readOnce.Wait(_wait));

        await AddItemAsync(database).WaitAsync(_wait);
        written.Set();

        Assert.Equal((1L, 1L), await read.WaitAsync(_wait));
        Assert.Equal(2L, await CountAsync(database));
    }

    // A deleted secret stays readable in -wal until a TRUNCATE checkpoint (F23); secure_delete clears it from the file.
    [Fact]
    public async Task TruncatingTheLogLeavesNoDeletedSecretOnDisk()
    {
        const string Secret = "deleted-secret-6f1c";
        var database = await WithItemsAsync(Open());
        await AddItemAsync(database, "kept");
        await AddItemAsync(database, Secret);
        await database.WriteAsync((connection, transaction) => Scalar(connection, $"DELETE FROM items WHERE value = '{Secret}';", transaction), CancellationToken.None);
        Assert.Contains(Secret, ReadText(DatabasePath + "-wal"), StringComparison.Ordinal);

        Assert.True(await database.TruncateWalAsync(CancellationToken.None));

        Assert.Equal(0, new FileInfo(DatabasePath + "-wal").Length);
        Assert.DoesNotContain(Secret, ReadText(DatabasePath), StringComparison.Ordinal);
        Assert.Equal(1L, await CountAsync(database));
    }

    private static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, System.Text.Encoding.Latin1);
        return reader.ReadToEnd();
    }

    private sealed class RefusingGuard : IKeyFileGuard
    {
        public FileStream CreatePrivate(string path) => throw new IOException("/secret");

        public void KeepPrivate(string path) => throw new IOException("/secret");
    }
}
