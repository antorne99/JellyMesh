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
/// Row 525: three recoveries in 24 hours, then the store stays damaged until Retry, and the last three damaged files stay.
/// </summary>
public sealed class RecoveryLimitTests : IDisposable
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(20);

    private readonly List<PluginDatabase> _opened = [];

    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 23, 3, 0, 0, TimeSpan.Zero));

    private readonly CapturingLogger<PluginDatabase> _logger = new();

    public RecoveryLimitTests() => Directory.CreateDirectory(Folder);

    // Under the temp folder, not the bind mount: WAL needs shared memory a host mount may not give.
    private string Folder { get; } = Path.Combine(Path.GetTempPath(), "jellymesh-limit-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(Folder, PluginDatabase.FileName);

    private string CountPath => Path.Combine(Folder, "jellymesh.db.recoveries");

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
        var database = new PluginDatabase(Folder, _logger, guard ?? KeyFileGuards.ForThisSystem(), SchemaMigrations.All, 1, _clock);
        _opened.Add(database);
        _ = database.Health;
        return database;
    }

    private async Task<StoreHealth> DamageAsync(PluginDatabase database)
    {
        var generation = database.Generation;
        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.ReadAsync<int>((_, _) => throw new SqliteException("malformed", 11), CancellationToken.None));
        var watch = Stopwatch.StartNew();
        while (database.Health is { State: StoreState.Damaged } health && !health.Reason!.Contains("recover", StringComparison.Ordinal))
        {
            Assert.True(watch.Elapsed < _wait, "The recovery did not end in time.");
            await Task.Delay(10);
        }

        Assert.Equal(database.Health.Suspended ? generation : generation + 1, database.Generation);
        return database.Health;
    }

    // Damages the store some times, an hour apart, each of which it recovers from.
    private async Task RecoverAsync(PluginDatabase database, int times)
    {
        for (var time = 0; time < times; time++)
        {
            _clock.Now += TimeSpan.FromHours(1);
            Assert.Equal(StoreHealth.Healthy, await DamageAsync(database));
        }
    }

    private void Spoil()
    {
        File.Delete(DatabasePath + "-wal");
        File.Delete(DatabasePath + "-shm");
        File.WriteAllText(DatabasePath, new string('x', 8192));
    }

    private string[] SetAside() => Directory.GetFiles(Folder, PluginDatabase.FileName + ".damaged-*").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    private string Plant(string name)
    {
        var path = Path.Combine(Folder, PluginDatabase.FileName + ".damaged-" + name);
        File.WriteAllText(path, "x");
        return Path.GetFileName(path);
    }

    private static Task<long> CountAsync(PluginDatabase database) => database.ReadAsync(
        (connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM sqlite_master;";
            return (long)command.ExecuteScalar()!;
        },
        CancellationToken.None);

    [Fact]
    public async Task AFourthDamageInADayStaysDamagedUntilRetry()
    {
        var database = Open();
        await RecoverAsync(database, 3);
        var aside = SetAside();
        _clock.Now += TimeSpan.FromHours(20);

        var health = await DamageAsync(database);

        Assert.Equal(StoreState.Damaged, health.State);
        Assert.Contains("malformed", health.Reason, StringComparison.Ordinal);
        Assert.Contains("3 times in 24 hours", health.Reason, StringComparison.Ordinal);
        await Task.Delay(100);
        Assert.Equal(aside, SetAside());
        await Assert.ThrowsAsync<StoreSuspendedException>(() => CountAsync(database));
        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.BackUpAsync(CancellationToken.None));
        Assert.Equal(4, database.Generation);

        Assert.Equal(StoreHealth.Healthy, database.Retry());
        Assert.True(await CountAsync(database) > 0);
        Assert.NotNull(database.Recovery);
        Assert.Equal(StoreState.Damaged, (await DamageAsync(database)).State);
    }

    [Fact]
    public async Task TheCountOutlivesARestart()
    {
        var first = Open();
        await RecoverAsync(first, 3);
        first.DismissRecovery();
        first.Dispose();

        var second = Open();
        Assert.Equal(StoreHealth.Healthy, second.Health);
        Assert.Equal(StoreState.Damaged, (await DamageAsync(second)).State);
        second.Dispose();

        Spoil();
        var aside = SetAside();
        var third = Open();
        Assert.Equal(StoreState.Damaged, third.Health.State);
        Assert.Contains("3 times in 24 hours", third.Health.Reason, StringComparison.Ordinal);
        Assert.Equal(aside, SetAside());
        Assert.Equal(StoreHealth.Healthy, third.Retry());
    }

    [Fact]
    public async Task ADamageADayAfterTheFirstRecoversByItself()
    {
        var database = Open();
        await RecoverAsync(database, 3);

        _clock.Now += TimeSpan.FromHours(22) - TimeSpan.FromMinutes(1);
        Assert.Equal(StoreState.Damaged, (await DamageAsync(database)).State);
        database.Dispose();
        _clock.Now += TimeSpan.FromMinutes(1);

        Assert.Equal(StoreHealth.Healthy, await DamageAsync(Open()));
    }

    // A clock set back leaves times ahead of it, which count for a day like those behind it and no longer.
    [Theory]
    [InlineData(23, false)]
    [InlineData(25, true)]
    public async Task ATimeAheadOfTheClockCountsWithinADay(int hoursBack, bool recovers)
    {
        var database = Open();
        for (var time = 0; time < 3; time++)
        {
            Assert.Equal(StoreHealth.Healthy, await DamageAsync(database));
        }

        _clock.Now -= TimeSpan.FromHours(hoursBack);

        Assert.Equal(recovers, !(await DamageAsync(database)).Suspended);
    }

    // The rest of a recovery cut short is not another one.
    [Fact]
    public async Task ARecoveryThatGoesOnCountsOnce()
    {
        var guard = new FailingGuard();
        var database = Open(guard);
        guard.Fails = path => path == DatabasePath;
        await DamageAsync(database);
        Assert.Equal(StoreState.Unavailable, database.Health.State);
        Assert.Equal(StoreState.Unavailable, database.Retry().State);
        guard.Fails = _ => false;
        Assert.Equal(StoreHealth.Healthy, database.Retry());

        await RecoverAsync(database, 2);

        Assert.Equal(StoreState.Damaged, (await DamageAsync(database)).State);
    }

    // What cannot be counted cannot be limited, so it waits for Retry, which starts the count again.
    [Fact]
    public async Task WithACountThatCannotBeReadTheStoreStaysDamagedUntilRetry()
    {
        var database = Open();
        await File.WriteAllTextAsync(CountPath, "x");

        var health = await DamageAsync(database);

        Assert.Equal(StoreState.Damaged, health.State);
        Assert.Contains("cannot be read", health.Reason, StringComparison.Ordinal);
        Assert.Empty(SetAside());
        Assert.Equal(StoreHealth.Healthy, database.Retry());
        await RecoverAsync(database, 2);
        Assert.Equal(StoreState.Damaged, (await DamageAsync(database)).State);
    }

    [Fact]
    public async Task WithACountThatCannotBeWrittenNothingMoves()
    {
        var guard = new FailingGuard();
        var database = Open(guard);
        guard.Fails = path => path.StartsWith(CountPath, StringComparison.Ordinal);

        var health = await DamageAsync(database);

        Assert.Equal(StoreState.Damaged, health.State);
        Assert.Contains("cannot be written", health.Reason, StringComparison.Ordinal);
        Assert.Empty(SetAside());
        Assert.Null(database.Recovery);
        Assert.False(File.Exists(Path.Combine(Folder, StoreBackups.RecoveryFileName)));
        Assert.Equal(StoreState.Damaged, database.Retry().State);
        Assert.Empty(SetAside());
        guard.Fails = _ => false;
        Assert.Equal(StoreHealth.Healthy, database.Retry());
    }

    [Fact]
    public async Task TheCountIsOwnerOnlyAndHoldsOnlyTimes()
    {
        await RecoverAsync(Open(), 2);

        Assert.Equal("""["2026-11-23T04:00:00+00:00","2026-11-23T05:00:00+00:00"]""", await File.ReadAllTextAsync(CountPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(CountPath));
        }
    }

    [Fact]
    public async Task OnlyTheLastThreeDamagedFilesAndTheirLogsStay()
    {
        var other = new[] { Plant("mine"), Plant("20260101000000.txt"), Plant("20260101000000-x"), Plant("2026010100000") };
        foreach (var name in new[] { "20260101000000", "20260101000000-wal", "20260102000000", "20260102000000-2", "20260102000000-2-journal" })
        {
            Plant(name);
        }

        var database = Open();
        Assert.Equal(9, SetAside().Length);

        await RecoverAsync(database, 1);
        var now = _clock.Now.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string[] kept = ["20260102000000", "20260102000000-2", "20260102000000-2-journal", now];
        Assert.Equal(other.Concat(kept.Select(name => PluginDatabase.FileName + ".damaged-" + name)).Order(StringComparer.Ordinal), SetAside().Where(name => !name.EndsWith("-wal", StringComparison.Ordinal)));
        Assert.DoesNotContain(PluginDatabase.FileName + ".damaged-20260101000000-wal", SetAside());
    }

    // The number after a date is a number: the tenth file of a second is newer than the ninth.
    [Fact]
    public async Task TheNightlySweepPrunesTooAndSparesTheFileOfTheBanner()
    {
        var database = Open();
        await RecoverAsync(database, 1);
        var banner = database.Recovery!.DamagedFile;
        foreach (var name in new[] { "20270101000000-9", "20270101000000-10", "20270101000000-11" })
        {
            Plant(name);
        }

        await database.DeleteExpiredDamagedAsync(CancellationToken.None);
        string[] newest = [PluginDatabase.FileName + ".damaged-20270101000000-10", PluginDatabase.FileName + ".damaged-20270101000000-11"];
        Assert.Equal(newest.Prepend(banner), SetAside().Where(name => !name.EndsWith("-wal", StringComparison.Ordinal)));

        database.DismissRecovery();
        Plant("20270101000000-12");
        await database.DeleteExpiredDamagedAsync(CancellationToken.None);
        Assert.DoesNotContain(banner, SetAside());
        Assert.Equal(3, SetAside().Length);
    }

    // Cannot make the files it is told to fail on.
    private sealed class FailingGuard : IKeyFileGuard
    {
        private readonly IKeyFileGuard _inner = KeyFileGuards.ForThisSystem();

        public Func<string, bool> Fails { get; set; } = _ => false;

        public FileStream CreatePrivate(string path) => Fails(path) ? throw new IOException("No space left on device") : _inner.CreatePrivate(path);

        public void KeepPrivate(string path) => _inner.KeepPrivate(path);
    }
}
