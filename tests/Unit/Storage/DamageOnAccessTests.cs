using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Storage;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Storage;

/// <summary>
/// Damage found by a read or a write after the open (row 512).
/// </summary>
public sealed class DamageOnAccessTests : IDisposable
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(20);

    private readonly List<PluginDatabase> _opened = [];

    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 23, 3, 0, 0, TimeSpan.Zero));

    public DamageOnAccessTests() => Directory.CreateDirectory(Folder);

    // Under the temp folder, not the bind mount: WAL needs shared memory a host mount may not give.
    private string Folder { get; } = Path.Combine(Path.GetTempPath(), "jellymesh-damage-" + Guid.NewGuid().ToString("N"));

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

    private PluginDatabase Open(IKeyFileGuard? guard = null, IReadOnlyList<SchemaMigration>? migrations = null, LegacyImport? legacy = null)
    {
        var database = new PluginDatabase(Folder, new CapturingLogger<PluginDatabase>(), guard ?? KeyFileGuards.ForThisSystem(), migrations ?? SchemaMigrations.All, 1, _clock, legacy);
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
            Scalar(connection, $"INSERT INTO items VALUES ('{value}');", transaction);
        },
        CancellationToken.None);

    private static string Items(SqliteConnection connection, SqliteTransaction? transaction) =>
        Convert.ToString(Scalar(connection, "SELECT group_concat(value) FROM (SELECT value FROM items ORDER BY rowid);", transaction), CultureInfo.InvariantCulture)!;

    private static Task<string> ItemsAsync(PluginDatabase database) => database.ReadAsync(Items, CancellationToken.None);

    // A copy of the first item, then a second one only in the database, with everything moved out of the log.
    private async Task<PluginDatabase> WithACopyAsync(IKeyFileGuard? guard = null)
    {
        var database = Open(guard);
        await AddItemAsync(database, "copied");
        await database.BackUpAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromHours(1);
        await AddItemAsync(database, "lost");
        Assert.True(await database.TruncateWalAsync(CancellationToken.None));
        return database;
    }

    // Overwrites the header in place, as a disk does: a new connection reads a file that is not a database.
    private byte[] Spoil()
    {
        Spoil(0, Enumerable.Repeat((byte)'x', 4096).ToArray());
        return File.ReadAllBytes(DatabasePath);
    }

    // Overwrites every page but the first in place: SQLite opens the file and reads its pages as malformed.
    private void SpoilPages() => Spoil(4096, Enumerable.Repeat((byte)0x5A, (int)(new FileInfo(DatabasePath).Length - 4096)).ToArray());

    private void Spoil(long position, byte[] garbage)
    {
        using var stream = new FileStream(DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        stream.Position = position;
        stream.Write(garbage);
        stream.Flush(true);
    }

    private string[] SetAside() => Directory.GetFiles(Folder, PluginDatabase.FileName + ".damaged-*").Where(file => !file.EndsWith("-wal", StringComparison.Ordinal)).Select(Path.GetFileName).ToArray()!;

    private static async Task UntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < _wait, "The condition did not come true in time.");
            await Task.Delay(10);
        }
    }

    private static Task RecoveredAsync(PluginDatabase database, long generation = 1) =>
        UntilAsync(() => database.Generation > generation && database.Health.State == StoreState.Healthy);

    [Fact]
    public async Task AReadThatFindsTheFileDamagedRestartsFromTheLatestCopy()
    {
        var database = await WithACopyAsync();
        var damaged = Spoil();

        var refused = await Assert.ThrowsAsync<StoreSuspendedException>(() => ItemsAsync(database));

        Assert.Equal(26, Assert.IsType<SqliteException>(refused.InnerException).SqliteErrorCode);
        await RecoveredAsync(database);
        Assert.Equal("copied", await ItemsAsync(database));
        Assert.Equal(2, database.Generation);
        var aside = Assert.Single(SetAside());
        Assert.Equal(damaged, await File.ReadAllBytesAsync(Path.Combine(Folder, aside)));
        Assert.Equal(new StoreRecovery(_clock.Now, aside, _clock.Now - TimeSpan.FromHours(1)), database.Recovery);
        await AddItemAsync(database, "after");
        Assert.Equal("copied,after", await ItemsAsync(database));
    }

    // Opened afresh, so the writer holds no page of the table in memory and meets the damage on disk.
    [Fact]
    public async Task AWriteThatFindsTheFileDamagedRestartsFromTheLatestCopy()
    {
        (await WithACopyAsync()).Dispose();
        var database = Open();
        Assert.Equal(StoreState.Healthy, database.Health.State);
        SpoilPages();

        var refused = await Assert.ThrowsAsync<StoreSuspendedException>(() => AddItemAsync(database, "refused"));

        Assert.Equal(11, Assert.IsType<SqliteException>(refused.InnerException).SqliteErrorCode);
        await RecoveredAsync(database);
        Assert.Equal("copied", await ItemsAsync(database));
        Assert.Single(SetAside());
        Assert.NotNull(database.Recovery);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(14)]
    public async Task AnyOtherSqliteErrorLeavesTheStoreHealthy(int code)
    {
        var database = await WithACopyAsync();
        var error = new SqliteException("transient", code);

        Assert.Same(error, await Assert.ThrowsAsync<SqliteException>(() => database.ReadAsync<int>((_, _) => throw error, CancellationToken.None)));
        Assert.Same(error, await Assert.ThrowsAsync<SqliteException>(() => database.WriteAsync((_, _) => throw error, CancellationToken.None)));

        Assert.Equal(StoreHealth.Healthy, database.Health);
        Assert.Equal("copied,lost", await ItemsAsync(database));
        Assert.Equal(1, database.Generation);
        Assert.Empty(SetAside());
        Assert.Null(database.Recovery);
    }

    // SQLITE_IOERR_CORRUPTFS: the file system failed, not the database.
    [Fact]
    public async Task AnErrorOfTheFileSystemLeavesTheStoreHealthy()
    {
        var database = await WithACopyAsync();

        await Assert.ThrowsAsync<SqliteException>(() => database.ReadAsync<int>((_, _) => throw new SqliteException("corrupt file system", 10, 8458), CancellationToken.None));

        Assert.Equal(StoreHealth.Healthy, database.Health);
        Assert.Empty(SetAside());
    }

    // SQLITE_CORRUPT_INDEX, which the check at open does not look for: the file is set aside without being asked again.
    [Fact]
    public async Task DamageThatTheIntegrityCheckDoesNotSeeIsSetAsideToo()
    {
        var database = await WithACopyAsync();

        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.ReadAsync<int>((_, _) => throw new SqliteException("malformed", 11, 779), CancellationToken.None));

        await RecoveredAsync(database);
        Assert.Single(SetAside());
        Assert.Equal("copied", await ItemsAsync(database));
    }

    [Fact]
    public async Task ManyCallsThatMeetTheDamageMakeOneRecovery()
    {
        var database = await WithACopyAsync();
        using var start = new ManualResetEventSlim();
        Spoil();
        var calls = Enumerable.Range(0, 24).Select(number => Task.Run(async () =>
        {
            start.Wait(_wait);
            try
            {
                if (number % 3 == 0)
                {
                    await database.WriteAsync((connection, transaction) => Items(connection, transaction), CancellationToken.None);
                }
                else
                {
                    Assert.Equal("copied", await ItemsAsync(database));
                }

                return true;
            }
            catch (StoreSuspendedException)
            {
                return false;
            }
        })).ToList();

        start.Set();
        var answers = await Task.WhenAll(calls).WaitAsync(_wait);

        Assert.Contains(false, answers);
        await RecoveredAsync(database);
        Assert.Equal("copied", await ItemsAsync(database));
        Assert.Equal(2, database.Generation);
        Assert.Single(SetAside());
    }

    // A read in progress keeps its snapshot, its generation and its files: nothing moves under it, and nothing new comes in.
    [Fact]
    public async Task TheRecoveryWaitsForTheReadsInProgress()
    {
        var database = await WithACopyAsync();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var read = database.ReadAsync(
            (connection, transaction) =>
            {
                var before = Items(connection, transaction);
                started.Set();
                release.Wait(_wait);
                return (before, Items(connection, transaction), database.Generation);
            },
            CancellationToken.None);
        Assert.True(started.Wait(_wait));

        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.ReadAsync<int>((_, _) => throw new SqliteException("malformed", 11), CancellationToken.None));

        Assert.Equal(StoreState.Damaged, database.Health.State);
        Assert.Contains("malformed", database.Health.Reason, StringComparison.Ordinal);
        await Assert.ThrowsAsync<StoreSuspendedException>(() => ItemsAsync(database));
        await Assert.ThrowsAsync<StoreSuspendedException>(() => AddItemAsync(database, "refused"));
        Assert.Equal(StoreState.Damaged, database.Retry().State);
        await Task.Delay(200);
        Assert.Equal(1, database.Generation);
        Assert.Empty(SetAside());

        release.Set();
        Assert.Equal(("copied,lost", "copied,lost", 1L), await read.WaitAsync(_wait));
        await RecoveredAsync(database);
        await AddItemAsync(database, "after");
        Assert.Equal("copied,after", await ItemsAsync(database));
        Assert.Single(SetAside());
    }

    [Fact]
    public async Task ANestedReadThatFindsTheDamageDoesNotBlockTheWriteAroundIt()
    {
        var database = await WithACopyAsync();

        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.WriteAsync(
            (_, _) => database.ReadAsync<int>((_, _) => throw new SqliteException("malformed", 11), CancellationToken.None).GetAwaiter().GetResult(),
            CancellationToken.None).WaitAsync(_wait));

        await RecoveredAsync(database);
        Assert.Equal("copied", await ItemsAsync(database));
    }

    // Retry goes on with the recovery instead of taking back a file an access read as damaged.
    [Fact]
    public async Task ARecoveryThatCannotStartStaysDamagedUntilRetrySucceeds()
    {
        var guard = new NoteFailingGuard();
        var database = await WithACopyAsync(guard);
        guard.Fails = true;

        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.ReadAsync<int>((_, _) => throw new SqliteException("malformed", 11), CancellationToken.None));
        await UntilAsync(() => database.Health.Reason!.Contains("recovery note", StringComparison.Ordinal));

        Assert.Equal(StoreState.Damaged, database.Retry().State);
        Assert.Equal(1, database.Generation);
        Assert.Empty(SetAside());
        guard.Fails = false;

        Assert.Equal(StoreState.Healthy, database.Retry().State);
        Assert.Equal(2, database.Generation);
        Assert.Equal("copied", await ItemsAsync(database));
        Assert.Single(SetAside());
    }

    [Fact]
    public async Task ADamageFoundWhileTheStoreClosesMovesNothing()
    {
        var database = await WithACopyAsync();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var read = database.ReadAsync<int>(
            (_, _) =>
            {
                started.Set();
                release.Wait(_wait);
                throw new SqliteException("malformed", 11);
            },
            CancellationToken.None);
        Assert.True(started.Wait(_wait));

        database.Dispose();
        release.Set();
        await Assert.ThrowsAsync<StoreSuspendedException>(() => read);
        await Task.Delay(200);

        Assert.Equal(StoreState.Unavailable, database.Health.State);
        Assert.Empty(SetAside());
        Assert.Equal("copied,lost", await ItemsAsync(Open()));
    }

    // The copies before stay byte by byte, and the damaged file becomes no copy.
    [Fact]
    public async Task ACopyOfADamagedFileIsNotMadeAndStartsTheRecovery()
    {
        (await WithACopyAsync()).Dispose();
        var copy = Assert.Single(Copies());
        var bytes = await File.ReadAllBytesAsync(copy);
        var database = Open();
        Assert.Equal(StoreState.Healthy, database.Health.State);
        SpoilPages();

        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.BackUpAsync(CancellationToken.None));

        await RecoveredAsync(database);
        Assert.Equal([copy], Copies());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(copy));
        Assert.Equal("copied", await ItemsAsync(database));
        Assert.Single(SetAside());
    }

    // VACUUM does not read the count of fragmented bytes of a page, which the integrity check does.
    [Fact]
    public async Task ADatabaseThatFailsItsCheckReplacesNoCopy()
    {
        var database = await WithACopyAsync();
        _clock.Now += TimeSpan.FromDays(1);
        await database.BackUpAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromDays(1);
        database.Dispose();
        database = Open();
        Assert.Equal(StoreState.Healthy, database.Health.State);
        var copies = Copies();
        var root = await database.ReadAsync((connection, transaction) => (long)Scalar(connection, "SELECT rootpage FROM sqlite_master WHERE name = 'items';", transaction)!, CancellationToken.None);
        Spoil(((root - 1) * 4096) + 7, [3]);

        await Assert.ThrowsAsync<InvalidDataException>(() => database.BackUpAsync(CancellationToken.None));

        Assert.Equal(2, copies.Length);
        Assert.Equal(copies, Copies());
        Assert.Empty(Directory.GetFiles(Folder, "*.partial"));
    }

    private string[] Copies() => Directory.GetFiles(Folder, PluginDatabase.FileName + ".backup-*").Order(StringComparer.Ordinal).ToArray();

    // Cannot write the recovery note while it fails.
    private sealed class NoteFailingGuard : IKeyFileGuard
    {
        private readonly IKeyFileGuard _inner = KeyFileGuards.ForThisSystem();

        public bool Fails { get; set; }

        public FileStream CreatePrivate(string path) =>
            Fails && Path.GetFileName(path).StartsWith(PluginDatabase.FileName + ".recovery", StringComparison.Ordinal) ? throw new IOException("The file is locked") : _inner.CreatePrivate(path);

        public void KeepPrivate(string path) => _inner.KeepPrivate(path);
    }
}