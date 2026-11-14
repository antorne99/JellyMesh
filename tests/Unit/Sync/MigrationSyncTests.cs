using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

// §10 row 420: the sync the automatic migration runs before its scan, and waits for, through the real sync service.
public sealed class MigrationSyncTests : IDisposable
{
    private static readonly TimeSpan _limit = TimeSpan.FromSeconds(10);

    private readonly SyncFixture _fixture = new();
    private readonly MigrationSync _sync;

    public MigrationSyncTests()
    {
        _sync = new MigrationSync(() => _fixture.Sync);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ItRunsASyncAndWaitsForIt()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(2));
        var server = await _fixture.AddServerAsync("A", "films");

        await _sync.SyncAsync(CancellationToken.None).WaitAsync(_limit);

        Assert.Equal(2, _fixture.Index.Snapshot.Find(server.Id)!.Items.Count);
        Assert.False(_fixture.Sync.GetStatus().IsRunning);
    }

    [Fact]
    public async Task ItWaitsForASyncAlreadyRunning()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        var server = await _fixture.AddServerAsync("A", "films");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Client.OnPage = (_, _) =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return RemoteFailure.None;
        };
        Assert.Equal(SyncStartResult.Started, _fixture.Sync.TryStart());
        await entered.Task.WaitAsync(_limit);

        var waiting = _sync.SyncAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);

        release.SetResult();
        await waiting.WaitAsync(_limit);

        Assert.False(_fixture.Sync.GetStatus().IsRunning);
        Assert.Single(_fixture.Index.Snapshot.Find(server.Id)!.Items);
    }

    [Fact]
    public async Task AnOfflineServerDoesNotHoldItBack()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        var server = await _fixture.AddServerAsync("A", "films");
        _fixture.Client.OnPage = (_, _) => RemoteFailure.Unreachable;

        await _sync.SyncAsync(CancellationToken.None).WaitAsync(_limit);

        Assert.Equal(RemoteFailure.Unreachable, _fixture.Index.Snapshot.Find(server.Id)!.LastFailure);
    }

    [Fact]
    public async Task AServerThatNeverAnswersIsLeftAtTheReadLimit()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        var server = await _fixture.AddServerAsync("A", "films");
        _fixture.Client.BeforeClock = (_, token) => Task.Delay(Timeout.Infinite, token);
        _fixture.Sync.ServerReadLimit = TimeSpan.FromMilliseconds(200);

        await _sync.SyncAsync(CancellationToken.None).WaitAsync(_limit);

        Assert.Equal(RemoteFailure.Timeout, _fixture.Index.Snapshot.Find(server.Id)!.LastFailure);
    }

    [Fact]
    public async Task ASuspendedSyncReturnsAtOnce()
    {
        using var fixture = new SyncFixture(key: null);
        var sync = new MigrationSync(() => fixture.Sync);

        await sync.SyncAsync(CancellationToken.None).WaitAsync(_limit);

        Assert.False(fixture.Sync.GetStatus().IsRunning);
    }

    [Fact]
    public async Task WaitingForTheIdleSyncReturnsAtOnce()
    {
        await _fixture.Sync.WaitIdleAsync(CancellationToken.None).WaitAsync(_limit);
    }
}
