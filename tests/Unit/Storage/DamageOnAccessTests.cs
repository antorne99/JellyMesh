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

    private readonly CapturingLogger<PluginDatabase> _logger = new();

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
        var database = new PluginDatabase(Folder, _logger, guard ?? KeyFileGuards.ForThisSystem(), migrations ?? SchemaMigrations.All, 1, _clock, legacy);
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

    // Gives two tables each other's index, which leaves every page readable.
    private static async Task SwapIndexesAsync(PluginDatabase database)
    {
        await database.WriteAsync(
            (connection, transaction) =>
            {
                Scalar(connection, "CREATE TABLE first (value TEXT); CREATE INDEX first_index ON first (value); INSERT INTO first VALUES ('a');", transaction);
                Scalar(connection, "CREATE TABLE second (value TEXT); CREATE INDEX second_index ON second (value); INSERT INTO second VALUES ('b');", transaction);
                var root = (long)Scalar(connection, "SELECT rootpage FROM sqlite_master WHERE name = 'first_index';", transaction)!;
                var other = (long)Scalar(connection, "SELECT rootpage FROM sqlite_master WHERE name = 'second_index';", transaction)!;
                Scalar(connection, "PRAGMA writable_schema = ON;", transaction);
                Scalar(connection, $"UPDATE sqlite_master SET rootpage = {other} WHERE name = 'first_index'; UPDATE sqlite_master SET rootpage = {root} WHERE name = 'second_index';", transaction);
                Scalar(connection, "PRAGMA writable_schema = RESET;", transaction);
            },
            CancellationToken.None);
        Assert.Equal("ok", await database.ReadAsync((connection, transaction) => Scalar(connection, "PRAGMA quick_check;", transaction), CancellationToken.None));
    }

    private static string CheckOf(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return (string)Scalar(connection, "PRAGMA integrity_check;")!;
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

    // A read that meets the damage meanwhile stops the copy before it takes the place of another.
    [Fact]
    public async Task ADamageFoundWhileACopyIsMadeReplacesNoCopy()
    {
        var guard = new CopyingGuard();
        var database = await WithACopyAsync(guard);
        _clock.Now += TimeSpan.FromDays(1);
        await database.BackUpAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromDays(1);
        var copies = Copies();
        guard.Copying = () => Assert.Throws<StoreSuspendedException>(() => database.ReadAsync<int>((_, _) => throw new SqliteException("malformed", 11), CancellationToken.None).GetAwaiter().GetResult());

        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.BackUpAsync(CancellationToken.None));

        await RecoveredAsync(database);
        Assert.Equal(2, copies.Length);
        Assert.Equal(copies, Copies());
        Assert.Empty(Directory.GetFiles(Folder, "*.partial"));
    }

    // Two indexes that hold each other's keys pass a quick check, and a delete that uses one fails as damage.
    [Fact]
    public async Task AnIndexThatDoesNotMatchItsTableIsRebuiltAndTheCopyMade()
    {
        var database = await WithACopyAsync();
        await SwapIndexesAsync(database);
        _clock.Now += TimeSpan.FromDays(1);

        var copy = await database.BackUpAsync(CancellationToken.None);

        Assert.Equal("ok", CheckOf(copy.FilePath));
        await database.WriteAsync((connection, transaction) => Scalar(connection, "DELETE FROM first WHERE rowid = 1;", transaction), CancellationToken.None);
        Assert.Equal("ok", await database.ReadAsync((connection, transaction) => Scalar(connection, "PRAGMA integrity_check;", transaction), CancellationToken.None));
        Assert.Equal(StoreHealth.Healthy, database.Health);
        Assert.Equal(1, database.Generation);
        Assert.Empty(SetAside());
        Assert.Null(database.Recovery);
    }

    // A store that waited for Retry past a restart is checked in full, and its own data stays when the indexes can be rebuilt.
    [Fact]
    public async Task RetryAfterARestartRebuildsTheIndexesOfTheFileItFinds()
    {
        var database = await WithACopyAsync();
        await SwapIndexesAsync(database);
        database.Dispose();
        await File.WriteAllTextAsync(Path.Combine(Folder, StoreBackups.HeldFileName), "\"damaged\"");

        var restarted = Open();
        Assert.Equal(new StoreHealth(StoreState.Damaged, "damaged"), restarted.Health);
        Assert.Equal(StoreHealth.Healthy, restarted.Retry());

        Assert.Equal("copied,lost", await ItemsAsync(restarted));
        Assert.Equal("ok", await restarted.ReadAsync((connection, transaction) => Scalar(connection, "PRAGMA integrity_check;", transaction), CancellationToken.None));
        Assert.Empty(SetAside());
        Assert.Null(restarted.Recovery);
    }

    [Fact]
    public async Task RebuildingTheIndexesWaitsForNoReadAndHoldsNone()
    {
        var database = await WithACopyAsync();
        await SwapIndexesAsync(database);
        _clock.Now += TimeSpan.FromDays(1);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var read = database.ReadAsync(
            (connection, transaction) =>
            {
                var before = Items(connection, transaction);
                started.Set();
                release.Wait(_wait);
                return (before, Items(connection, transaction));
            },
            CancellationToken.None);
        Assert.True(started.Wait(_wait));

        var copy = await database.BackUpAsync(CancellationToken.None).WaitAsync(_wait);

        Assert.Equal("copied,lost", await ItemsAsync(database));
        release.Set();
        Assert.Equal(("copied,lost", "copied,lost"), await read.WaitAsync(_wait));
        Assert.Equal("ok", CheckOf(copy.FilePath));
    }

    // An index at the page of a table, which rebuilding would empty, and a unique one over two equal rows.
    [Theory]
    [InlineData("CREATE INDEX broken ON first (value);", "UPDATE sqlite_master SET rootpage = (SELECT rootpage FROM sqlite_master WHERE name = 'first') WHERE name = 'broken';")]
    [InlineData("CREATE INDEX broken ON first (value);", "UPDATE sqlite_master SET sql = 'CREATE UNIQUE INDEX broken ON first (value)' WHERE name = 'broken';")]
    public async Task ADatabaseThatRebuiltIndexesDoNotMendIsReplacedAsItWas(string index, string breaks)
    {
        var database = await WithACopyAsync();
        await database.WriteAsync(
            (connection, transaction) =>
            {
                Scalar(connection, "CREATE TABLE first (value TEXT); INSERT INTO first VALUES ('a'), ('a');" + index, transaction);
                Scalar(connection, "PRAGMA writable_schema = ON;", transaction);
                Scalar(connection, breaks, transaction);
                Scalar(connection, "PRAGMA writable_schema = RESET;", transaction);
            },
            CancellationToken.None);
        Assert.True(await database.TruncateWalAsync(CancellationToken.None));
        var damaged = await File.ReadAllBytesAsync(DatabasePath);
        var copies = Copies();
        var bytes = await File.ReadAllBytesAsync(Assert.Single(copies));
        _clock.Now += TimeSpan.FromDays(1);

        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.BackUpAsync(CancellationToken.None));

        await RecoveredAsync(database);
        Assert.Equal("copied", await ItemsAsync(database));
        Assert.Equal(copies, Copies());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(copies[0]));
        Assert.Equal(damaged, await File.ReadAllBytesAsync(Path.Combine(Folder, Assert.Single(SetAside()))));
        Assert.Empty(Directory.GetFiles(Folder, "*.partial*"));
    }

    // A copy of an earlier version, which a restore would find damaged again: the restored file is mended, not the copy.
    [Fact]
    public async Task ACopyWithAnIndexThatDoesNotMatchItsTableIsMendedWhenItIsRestored()
    {
        var database = await WithACopyAsync();
        _clock.Now += TimeSpan.FromDays(1);
        var copy = await database.BackUpAsync(CancellationToken.None);
        await SwapIndexesAsync(database);
        Assert.True(await database.TruncateWalAsync(CancellationToken.None));
        File.Copy(DatabasePath, copy.FilePath, overwrite: true);
        var bytes = await File.ReadAllBytesAsync(copy.FilePath);

        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.WriteAsync((connection, transaction) => Scalar(connection, "DELETE FROM first WHERE rowid = 1;", transaction), CancellationToken.None));

        await RecoveredAsync(database);
        Assert.Equal("copied,lost", await ItemsAsync(database));
        Assert.Equal(_clock.Now, database.Recovery?.CopyDate);
        Assert.Equal("ok", await database.ReadAsync((connection, transaction) => Scalar(connection, "PRAGMA integrity_check;", transaction), CancellationToken.None));
        await database.WriteAsync((connection, transaction) => Scalar(connection, "DELETE FROM first WHERE rowid = 1;", transaction), CancellationToken.None);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(copy.FilePath));
    }

    [Fact]
    public async Task ACopyThatRebuiltIndexesDoNotMendIsPassedOver()
    {
        var database = await WithACopyAsync();
        _clock.Now += TimeSpan.FromDays(1);
        var copy = await database.BackUpAsync(CancellationToken.None);
        await database.WriteAsync(
            (connection, transaction) =>
            {
                Scalar(connection, "CREATE TABLE first (value TEXT); INSERT INTO first VALUES ('a'), ('a'); CREATE INDEX broken ON first (value); PRAGMA writable_schema = ON;", transaction);
                Scalar(connection, "UPDATE sqlite_master SET sql = 'CREATE UNIQUE INDEX broken ON first (value)' WHERE name = 'broken';", transaction);
                Scalar(connection, "PRAGMA writable_schema = RESET;", transaction);
            },
            CancellationToken.None);
        Assert.True(await database.TruncateWalAsync(CancellationToken.None));
        File.Copy(DatabasePath, copy.FilePath, overwrite: true);
        var bytes = await File.ReadAllBytesAsync(copy.FilePath);

        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.ReadAsync<int>((_, _) => throw new SqliteException("malformed", 11), CancellationToken.None));

        await RecoveredAsync(database);
        Assert.Equal("copied", await ItemsAsync(database));
        Assert.Equal(_clock.Now - TimeSpan.FromHours(25), database.Recovery?.CopyDate);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(copy.FilePath));
    }

    // The check at open does not see every damage, so a file an access read as damaged never comes back by itself.
    [Fact]
    public async Task ARetryAfterAFailedOpenStillSetsTheDamagedFileAside()
    {
        var guard = new PrivacyFailingGuard();
        var database = await WithACopyAsync(guard);
        guard.Fails = true;

        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.ReadAsync<int>((_, _) => throw new SqliteException("malformed", 11), CancellationToken.None));
        await UntilAsync(() => database.Health.State == StoreState.Unavailable);
        Assert.Empty(SetAside());
        guard.Fails = false;

        Assert.Equal(StoreState.Healthy, database.Retry().State);
        Assert.Equal("copied", await ItemsAsync(database));
        Assert.Single(SetAside());
    }

    // The banner is of a restore that ended: until then no copy date is known, and the state says what is wrong.
    [Fact]
    public async Task ARestoreNotEndedYetShowsNoBanner()
    {
        var guard = new DatabaseFailingGuard(DatabasePath);
        var database = await WithACopyAsync(guard);
        guard.Fails = true;

        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.ReadAsync<int>((_, _) => throw new SqliteException("malformed", 11), CancellationToken.None));
        await UntilAsync(() => database.Health.State == StoreState.Unavailable);

        Assert.Null(database.Recovery);
        guard.Fails = false;

        Assert.Equal(StoreState.Healthy, database.Retry().State);
        Assert.Equal("copied", await ItemsAsync(database));
        Assert.Equal(_clock.Now - TimeSpan.FromHours(1), database.Recovery?.CopyDate);
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
    public async Task ADatabaseThatFailsItsCheckReplacesNoCopyAndStartsTheRecovery()
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

        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.BackUpAsync(CancellationToken.None));

        await RecoveredAsync(database);
        Assert.Equal("copied,lost", await ItemsAsync(database));
        Assert.Single(SetAside());
        Assert.Equal(2, copies.Length);
        Assert.Equal(copies, Copies());
        Assert.Empty(Directory.GetFiles(Folder, "*.partial"));
    }

    // The table of the migration mark reads a page of an index, as after a write of the disk to the wrong place.
    [Fact]
    public async Task ADamageFoundByTheMigrationOfTheOlderDataRestartsFromTheLatestCopy()
    {
        (await WithACopyAsync()).Dispose();
        var spoiled = false;
        var step = new SchemaMigration(SchemaMigrations.Latest + 1, (connection, transaction) =>
        {
            if (!spoiled)
            {
                spoiled = true;
                Scalar(connection, "PRAGMA writable_schema = ON; UPDATE sqlite_master SET rootpage = (SELECT rootpage FROM sqlite_master WHERE type = 'index' LIMIT 1) WHERE name = 'legacy_import'; PRAGMA writable_schema = RESET;", transaction);
            }
        });

        var database = Open(migrations: [.. SchemaMigrations.All, step], legacy: new LegacyImport(Folder, null, new CapturingLogger<LegacyImport>()));

        Assert.Equal(StoreState.Healthy, database.Health.State);
        Assert.Equal("copied", await ItemsAsync(database));
        Assert.Single(SetAside());
        Assert.NotNull(database.Recovery);
    }

    private string[] Copies() => Directory.GetFiles(Folder, PluginDatabase.FileName + ".backup-*").Order(StringComparer.Ordinal).ToArray();

    // Row 526: the log asks for both lists, as the page does, after a copy and after an empty restart.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARestartAsksToCheckServersAndPeers(bool withACopy)
    {
        var database = withACopy ? await WithACopyAsync() : Open();
        _ = database.Health;
        database.Dispose();
        File.Delete(DatabasePath + "-wal");
        File.Delete(DatabasePath + "-shm");
        await File.WriteAllTextAsync(DatabasePath, new string('x', 8192));

        Assert.Equal(StoreHealth.Healthy, Open().Health);

        Assert.Single(_logger.Lines, line => line.Contains(withACopy ? "from the copy" : "restarted empty", StringComparison.Ordinal) && line.Contains("Check the servers and peers", StringComparison.Ordinal));
    }

    // Cannot write the recovery note while it fails.
    private sealed class NoteFailingGuard : IKeyFileGuard
    {
        private readonly IKeyFileGuard _inner = KeyFileGuards.ForThisSystem();

        public bool Fails { get; set; }

        public FileStream CreatePrivate(string path) =>
            Fails && Path.GetFileName(path).StartsWith(PluginDatabase.FileName + ".recovery", StringComparison.Ordinal) ? throw new IOException("The file is locked") : _inner.CreatePrivate(path);

        public void KeepPrivate(string path) => _inner.KeepPrivate(path);
    }

    // Runs something as a copy starts.
    private sealed class CopyingGuard : IKeyFileGuard
    {
        private readonly IKeyFileGuard _inner = KeyFileGuards.ForThisSystem();

        public Action? Copying { get; set; }

        public FileStream CreatePrivate(string path)
        {
            if (path.EndsWith(".partial", StringComparison.Ordinal))
            {
                Copying?.Invoke();
            }

            return _inner.CreatePrivate(path);
        }

        public void KeepPrivate(string path) => _inner.KeepPrivate(path);
    }

    // Cannot keep a file private while it fails.
    private sealed class PrivacyFailingGuard : IKeyFileGuard
    {
        private readonly IKeyFileGuard _inner = KeyFileGuards.ForThisSystem();

        public bool Fails { get; set; }

        public FileStream CreatePrivate(string path) => _inner.CreatePrivate(path);

        public void KeepPrivate(string path)
        {
            if (Fails)
            {
                throw new IOException("The permissions cannot be changed");
            }

            _inner.KeepPrivate(path);
        }
    }

    // Cannot make the database file while it fails, as on a full disk.
    private sealed class DatabaseFailingGuard(string databasePath) : IKeyFileGuard
    {
        private readonly IKeyFileGuard _inner = KeyFileGuards.ForThisSystem();

        public bool Fails { get; set; }

        public FileStream CreatePrivate(string path) =>
            Fails && path == databasePath ? throw new IOException("No space left on device") : _inner.CreatePrivate(path);

        public void KeepPrivate(string path) => _inner.KeepPrivate(path);
    }
}