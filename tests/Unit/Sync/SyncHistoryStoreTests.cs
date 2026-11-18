using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Storage;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Storage;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

public sealed class SyncHistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 3, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private SyncHistoryStore Store(CapturingLogger<SyncHistoryStore>? logger = null) => new(_database, logger ?? new CapturingLogger<SyncHistoryStore>());

    private static SyncRun Run(int minute, SyncRunOutcome outcome = SyncRunOutcome.Succeeded) => new(
        Start.AddMinutes(minute),
        1500,
        SyncRunKind.Incremental,
        outcome,
        [new SyncRunServer(Guid.Parse("8e3f1c2a-4b5d-4e6f-8a9b-0c1d2e3f4a5b"), "Friend", 12, 3, 2, 1, 0, null)])
    {
        Created = 3,
        Updated = 2,
        Removed = 1
    };

    [Fact]
    public void AnEmptyDatabaseMeansNoRuns()
    {
        using var store = Store();

        Assert.Empty(store.Runs);
        Assert.False(store.IsDamaged);
    }

    [Fact]
    public async Task RunsSurviveARestart_NewestFirst()
    {
        var first = Run(0);
        var second = Run(15, SyncRunOutcome.PartlyFailed) with
        {
            Servers = [new SyncRunServer(Guid.NewGuid(), "Other", 0, 0, 0, 0, 2, RemoteFailure.Unreachable)]
        };
        using (var store = Store())
        {
            await store.AddAsync(first, CancellationToken.None);
            await store.AddAsync(second, CancellationToken.None);
        }

        using var reloaded = Store();

        Assert.Equal(2, reloaded.Runs.Count);
        Assert.Equal(second.StartedUtc, reloaded.Runs[0].StartedUtc);
        Assert.Equal(second.Outcome, reloaded.Runs[0].Outcome);
        Assert.Equal(second.Servers, reloaded.Runs[0].Servers);
        Assert.Equal(first.Servers, reloaded.Runs[1].Servers);
        Assert.Equal(first.DurationMilliseconds, reloaded.Runs[1].DurationMilliseconds);
        Assert.Equal(3, reloaded.Runs[1].Created);
    }

    [Fact]
    public async Task OnlyTheLastFiftyRunsAreKept()
    {
        using (var store = Store())
        {
            for (var minute = 0; minute < SyncHistoryStore.Capacity + 1; minute++)
            {
                await store.AddAsync(Run(minute), CancellationToken.None);
            }

            Assert.Equal(SyncHistoryStore.Capacity, store.Runs.Count);
            Assert.Equal(Start.AddMinutes(SyncHistoryStore.Capacity), store.Runs[0].StartedUtc);
            Assert.Equal(Start.AddMinutes(1), store.Runs[^1].StartedUtc);
        }

        using var reloaded = Store();
        Assert.Equal(SyncHistoryStore.Capacity, reloaded.Runs.Count);
    }

    [Fact]
    public async Task TheLastSevenReconciliationsAreKept_PastTheLastFifty()
    {
        using (var store = Store())
        {
            for (var minute = 0; minute < 8; minute++)
            {
                await store.AddAsync(Reconciliation(minute), CancellationToken.None);
            }

            for (var minute = 8; minute < 58; minute++)
            {
                await store.AddAsync(Run(minute), CancellationToken.None);
            }

            AssertKept(store, [.. Enumerable.Range(8, 50).Reverse(), .. Enumerable.Range(1, 7).Reverse()]);
        }

        using var reloaded = Store();
        AssertKept(reloaded, [.. Enumerable.Range(8, 50).Reverse(), .. Enumerable.Range(1, 7).Reverse()]);
    }

    [Fact]
    public async Task AReconciliationAmongTheLastFifty_CountsTowardTheSeven_AndShowsOnce()
    {
        using var store = Store();
        for (var minute = 0; minute < 8; minute++)
        {
            await store.AddAsync(Reconciliation(minute), CancellationToken.None);
        }

        for (var minute = 8; minute < 58; minute++)
        {
            await store.AddAsync(minute == 53 ? Reconciliation(minute) : Run(minute), CancellationToken.None);
        }

        AssertKept(store, [.. Enumerable.Range(8, 50).Reverse(), .. Enumerable.Range(2, 6).Reverse()]);
    }

    private static SyncRun Reconciliation(int minute) => Run(minute) with { Kind = SyncRunKind.Reconciliation };

    private static void AssertKept(SyncHistoryStore store, int[] minutes) =>
        Assert.Equal(minutes.Select(minute => Start.AddMinutes(minute)), store.Runs.Select(run => run.StartedUtc));

    [Fact]
    public async Task EachRunIsARowWithAServerPerRow_AndTheDroppedRunsLeaveNoRow()
    {
        using var store = Store();
        for (var minute = 0; minute < SyncHistoryStore.Capacity + 2; minute++)
        {
            await store.AddAsync(Run(minute), CancellationToken.None);
        }

        Assert.Equal(SyncHistoryStore.Capacity, _database.Count("sync_runs"));
        Assert.Equal(SyncHistoryStore.Capacity, _database.Count("sync_run_servers"));
    }

    [Fact]
    public async Task WhileTheDatabaseIsSuspended_ARunStillShows_TheSaveThrows_AndTheNextSaveKeepsIt()
    {
        using (var earlier = Store())
        {
            await earlier.AddAsync(Run(0), CancellationToken.None);
        }

        _database.Suspended = true;
        using var store = Store();

        await Assert.ThrowsAsync<StoreSuspendedException>(() => store.AddAsync(Run(1), CancellationToken.None));

        Assert.True(store.IsDamaged);
        AssertKept(store, [1]);

        _database.Suspended = false;
        Assert.False(store.IsDamaged);
        AssertKept(store, [1, 0]);
        await store.AddAsync(Run(2), CancellationToken.None);
        using var reloaded = Store();
        AssertKept(reloaded, [2, 1, 0]);
    }

    // A database opened again, as from a copy, may hold another history: it is read again before use.
    [Fact]
    public async Task AfterTheDatabaseIsOpenedAgainTheHistoryIsReadAgain()
    {
        using var store = Store();
        await store.AddAsync(Run(0), CancellationToken.None);
        await _database.ExecuteAsync("DELETE FROM sync_run_servers; DELETE FROM sync_runs;");

        _database.Reopen();

        Assert.Empty(store.Runs);
    }
}
