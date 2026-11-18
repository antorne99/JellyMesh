using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Storage;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Storage;

public sealed class StoreBackupTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly List<PluginDatabase> _opened = [];

    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 18, 3, 0, 0, TimeSpan.Zero));

    public StoreBackupTests() => Directory.CreateDirectory(Folder);

    // Under the temp folder, not the bind mount: WAL needs shared memory a host mount may not give.
    private string Folder { get; } = Path.Combine(Path.GetTempPath(), "jellymesh-backups-" + Guid.NewGuid().ToString("N"));

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

    private PluginDatabase Open(IKeyFileGuard? guard = null)
    {
        var database = new PluginDatabase(
            Folder,
            new CapturingLogger<PluginDatabase>(),
            guard ?? KeyFileGuards.ForThisSystem(),
            SchemaMigrations.All,
            1,
            _clock);
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

    private static Task AddItemAsync(PluginDatabase database, string value) => database.WriteAsync(
        (connection, transaction) =>
        {
            Scalar(connection, "CREATE TABLE IF NOT EXISTS items (value TEXT);", transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO items VALUES ($value);";
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        },
        CancellationToken.None);

    private static List<string> ReadItems(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM items ORDER BY rowid;";
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private string[] Copies() => Directory.GetFiles(Folder, PluginDatabase.FileName + ".backup-*").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    [Fact]
    public async Task ACopyHoldsTheDatabaseUnderTheNameOfItsTime()
    {
        var database = Open();
        await AddItemAsync(database, "kept");

        var copy = await database.BackUpAsync(CancellationToken.None);

        Assert.Equal(Path.Combine(Folder, "jellymesh.db.backup-20261118030000"), copy.FilePath);
        Assert.Equal(_clock.Now, copy.CreatedAt);
        Assert.Equal(["kept"], ReadItems(copy.FilePath));
    }

    // VACUUM INTO makes its file 0644 (F23), so the store makes it private first.
    [Fact]
    public async Task ACopyIsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var database = Open();

        var copy = await database.BackUpAsync(CancellationToken.None);

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(copy.FilePath));
    }

    [Fact]
    public async Task OnlyTheLastTwoCopiesAreKept()
    {
        var database = Open();
        for (var night = 0; night < 3; night++)
        {
            await AddItemAsync(database, "night " + night);
            await database.BackUpAsync(CancellationToken.None);
            _clock.Now += TimeSpan.FromDays(1);
        }

        Assert.Equal(["jellymesh.db.backup-20261119030000", "jellymesh.db.backup-20261120030000"], Copies());
        Assert.Equal(["night 0", "night 1", "night 2"], ReadItems(Path.Combine(Folder, "jellymesh.db.backup-20261120030000")));
    }

    // The key is a file of its own, out of the database, so no copy carries it.
    [Fact]
    public async Task ACopyHoldsNoKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await File.WriteAllBytesAsync(Path.Combine(Folder, "credential.key"), key);
        var database = Open();
        await AddItemAsync(database, "encrypted credentials");

        var copy = await database.BackUpAsync(CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(copy.FilePath);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(key));
    }

    [Fact]
    public async Task ACopyOfTheSameSecondReplacesTheEarlierOne()
    {
        var database = Open();
        await database.BackUpAsync(CancellationToken.None);
        await AddItemAsync(database, "later");

        var copy = await database.BackUpAsync(CancellationToken.None);

        Assert.Equal(["jellymesh.db.backup-20261118030000"], Copies());
        Assert.Equal(["later"], ReadItems(copy.FilePath));
    }

    // A copy that fails leaves no empty file a recovery would take for the latest one, and the copies before it stay.
    [Fact]
    public async Task AFailedCopyLeavesNothingAndKeepsTheCopiesBefore()
    {
        var guard = new SpoilingGuard();
        var database = Open(guard);
        await database.BackUpAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromDays(1);
        await database.BackUpAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromDays(1);

        guard.Spoil = true;
        await Assert.ThrowsAsync<SqliteException>(() => database.BackUpAsync(CancellationToken.None));

        Assert.Equal(["jellymesh.db.backup-20261118030000", "jellymesh.db.backup-20261119030000"], Copies());
        Assert.Equal(StoreState.Healthy, database.Health.State);
    }

    // A clock set back keeps the copy just written, and the copies dated after it go.
    [Fact]
    public async Task AClockSetBackKeepsTheNewCopyAndDropsTheCopiesDatedAfterIt()
    {
        var database = Open();
        var start = _clock.Now;
        _clock.Now = start + TimeSpan.FromDays(2);
        await database.BackUpAsync(CancellationToken.None);
        _clock.Now = start + TimeSpan.FromDays(3);
        await database.BackUpAsync(CancellationToken.None);
        _clock.Now = start;

        await database.BackUpAsync(CancellationToken.None);

        Assert.Equal(["jellymesh.db.backup-20261118030000"], Copies());
    }

    [Fact]
    public async Task ASuspendedDatabaseMakesNoCopy()
    {
        Directory.CreateDirectory(DatabasePath);
        var database = Open();

        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.BackUpAsync(CancellationToken.None));

        Assert.Empty(Copies());
    }

    private static async Task<byte[]> SpoilAsync(string path)
    {
        var garbage = new byte[8192];
        Array.Fill(garbage, (byte)'x');
        await File.WriteAllBytesAsync(path, garbage);
        return garbage;
    }

    // Three nights of items, a copy after the first two, and the database left damaged on the third.
    private async Task<byte[]> DamageAfterTwoCopiesAsync()
    {
        var database = Open();
        await AddItemAsync(database, "first");
        await database.BackUpAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromDays(1);
        await AddItemAsync(database, "second");
        await database.BackUpAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromDays(1);
        await AddItemAsync(database, "lost");
        database.Dispose();
        _opened.Remove(database);
        return await SpoilAsync(DatabasePath);
    }

    private async Task<List<string>> ItemsAsync(PluginDatabase database) => await database.ReadAsync(
        (connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT value FROM items ORDER BY rowid;";
            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read())
            {
                values.Add(reader.GetString(0));
            }

            return values;
        },
        CancellationToken.None);

    [Fact]
    public async Task ADamagedFileIsSetAsideAndTheStoreRestartsFromTheLatestCopy()
    {
        var damaged = await DamageAfterTwoCopiesAsync();

        var database = Open();

        Assert.Equal(StoreState.Healthy, database.Health.State);
        Assert.Equal(["first", "second"], await ItemsAsync(database));
        Assert.Equal(damaged, await File.ReadAllBytesAsync(Path.Combine(Folder, "jellymesh.db.damaged-20261120030000")));
        Assert.Equal(
            new StoreRecovery(_clock.Now, "jellymesh.db.damaged-20261120030000", new DateTimeOffset(2026, 11, 19, 3, 0, 0, TimeSpan.Zero)),
            database.Recovery);
        Assert.Equal(["jellymesh.db.backup-20261118030000", "jellymesh.db.backup-20261119030000"], Copies());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(DatabasePath));
        }
    }

    [Fact]
    public async Task AFileThatFailsTheIntegrityCheckIsSetAsideToo()
    {
        var database = Open();
        await database.WriteAsync(
            (connection, transaction) =>
            {
                Scalar(connection, "CREATE TABLE later (value TEXT); CREATE INDEX later_value ON later (value);", transaction);
                for (var row = 0; row < 200; row++)
                {
                    Scalar(connection, $"INSERT INTO later VALUES ('{new string('v', 200)}{row}');", transaction);
                }
            },
            CancellationToken.None);
        await database.BackUpAsync(CancellationToken.None);
        database.Dispose();
        _opened.Remove(database);

        // Overwrites the middle of the file, past the header and the schema page.
        var bytes = await File.ReadAllBytesAsync(DatabasePath);
        Array.Fill(bytes, (byte)0x5A, bytes.Length / 2, 4096);
        await File.WriteAllBytesAsync(DatabasePath, bytes);

        var recovered = Open();

        Assert.Equal(StoreState.Healthy, recovered.Health.State);
        Assert.Equal(_clock.Now, recovered.Recovery?.CopyDate);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(Folder, "jellymesh.db.damaged-20261118030000")));
    }

    [Fact]
    public async Task WithoutACopyTheStoreRestartsEmptyWithTheWarning()
    {
        var damaged = await SpoilAsync(DatabasePath);

        var database = Open();

        Assert.Equal(StoreState.Healthy, database.Health.State);
        Assert.Equal(new StoreRecovery(_clock.Now, "jellymesh.db.damaged-20261118030000", null), database.Recovery);
        Assert.Equal(damaged, await File.ReadAllBytesAsync(Path.Combine(Folder, "jellymesh.db.damaged-20261118030000")));
        var version = await database.ReadAsync((connection, transaction) => Scalar(connection, "PRAGMA user_version;", transaction), CancellationToken.None);
        Assert.Equal((long)SchemaMigrations.Latest, version);
        Assert.Equal(0L, await database.ReadAsync((connection, transaction) => Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE name = 'items';", transaction), CancellationToken.None));
    }

    [Fact]
    public async Task ADamagedCopyIsPassedOverForTheOneBefore()
    {
        await DamageAfterTwoCopiesAsync();
        await SpoilAsync(Path.Combine(Folder, "jellymesh.db.backup-20261119030000"));

        var database = Open();

        Assert.Equal(["first"], await ItemsAsync(database));
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 3, 0, 0, TimeSpan.Zero), database.Recovery?.CopyDate);
    }

    // A copy dated after the recovery comes from a clock that ran ahead, so the newest one dated before it wins.
    [Fact]
    public async Task ARestorePrefersTheNewestCopyNotDatedInTheFuture()
    {
        await DamageAfterTwoCopiesAsync();
        File.Copy(Path.Combine(Folder, "jellymesh.db.backup-20261118030000"), Path.Combine(Folder, "jellymesh.db.backup-20261201030000"));

        var database = Open();

        Assert.Equal(["first", "second"], await ItemsAsync(database));
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 3, 0, 0, TimeSpan.Zero), database.Recovery?.CopyDate);
    }

    // A log of the damaged file must not be read into the copy that takes its place.
    [Fact]
    public async Task ThePendingLogGoesAsideWithTheDamagedFile()
    {
        await DamageAfterTwoCopiesAsync();
        var log = RandomNumberGenerator.GetBytes(4096);
        await File.WriteAllBytesAsync(DatabasePath + "-wal", log);
        await File.WriteAllBytesAsync(DatabasePath + "-shm", new byte[32768]);

        var database = Open();

        Assert.Equal(["first", "second"], await ItemsAsync(database));
        Assert.Equal(log, await File.ReadAllBytesAsync(Path.Combine(Folder, "jellymesh.db.damaged-20261120030000-wal")));
    }

    [Fact]
    public async Task TheRecoveryOutlivesARestartUntilItIsDismissed()
    {
        await DamageAfterTwoCopiesAsync();
        var first = Open();
        var recovery = first.Recovery;
        Assert.NotNull(recovery);
        first.Dispose();
        _opened.Remove(first);

        var restarted = Open();
        Assert.Equal(recovery, restarted.Recovery);

        restarted.DismissRecovery();
        Assert.Null(restarted.Recovery);
        restarted.Dispose();
        _opened.Remove(restarted);

        Assert.Null(Open().Recovery);
    }

    // A copy cut short, as by a restart in the middle of the night, is never taken for the latest one.
    [Fact]
    public async Task AnInterruptedCopyIsPassedOverAndClearedByTheNextOne()
    {
        await DamageAfterTwoCopiesAsync();
        await File.WriteAllBytesAsync(Path.Combine(Folder, "jellymesh.db.backup-20261120030000.partial"), []);
        await File.WriteAllBytesAsync(Path.Combine(Folder, "jellymesh.db.backup-20261121030000"), []);

        var database = Open();

        Assert.Equal(["first", "second"], await ItemsAsync(database));
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 3, 0, 0, TimeSpan.Zero), database.Recovery?.CopyDate);

        await database.BackUpAsync(CancellationToken.None);
        Assert.Empty(Directory.GetFiles(Folder, "*.partial"));
    }

    // A recovery stopped after the set-aside, for example by a full disk, goes on at the next open and never starts empty.
    [Fact]
    public async Task ARecoveryCutShortGoesOnAtTheNextOpen()
    {
        await DamageAfterTwoCopiesAsync();
        var guard = new FailingDatabaseGuard(DatabasePath);

        var failed = Open(guard);

        Assert.Equal(StoreState.Unavailable, failed.Health.State);
        Assert.False(File.Exists(DatabasePath));
        failed.Dispose();
        _opened.Remove(failed);

        var database = Open();

        Assert.Equal(StoreState.Healthy, database.Health.State);
        Assert.Equal(["first", "second"], await ItemsAsync(database));
        Assert.Equal(new StoreRecovery(_clock.Now, "jellymesh.db.damaged-20261120030000", new DateTimeOffset(2026, 11, 19, 3, 0, 0, TimeSpan.Zero)), database.Recovery);
    }

    [Fact]
    public void AHealthyOpenHasNoRecovery()
    {
        var database = Open();

        Assert.Equal(StoreState.Healthy, database.Health.State);
        Assert.Null(database.Recovery);
        Assert.Empty(Directory.GetFiles(Folder, "*damaged*"));
    }

    // Fills each new file once spoiled, which VACUUM INTO refuses (F23).
    private sealed class SpoilingGuard : IKeyFileGuard
    {
        private readonly IKeyFileGuard _inner = KeyFileGuards.ForThisSystem();

        public bool Spoil { get; set; }

        public FileStream CreatePrivate(string path)
        {
            var stream = _inner.CreatePrivate(path);
            if (Spoil)
            {
                stream.Write(new byte[8192]);
                stream.Flush();
            }

            return stream;
        }

        public void KeepPrivate(string path) => _inner.KeepPrivate(path);
    }

    // Cannot create the database file, as on a full disk.
    private sealed class FailingDatabaseGuard(string databasePath) : IKeyFileGuard
    {
        private readonly IKeyFileGuard _inner = KeyFileGuards.ForThisSystem();

        public FileStream CreatePrivate(string path) =>
            path == databasePath ? throw new IOException("No space left on device") : _inner.CreatePrivate(path);

        public void KeepPrivate(string path) => _inner.KeepPrivate(path);
    }
}
