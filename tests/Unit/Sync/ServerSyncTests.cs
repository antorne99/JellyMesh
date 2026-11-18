using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

// Row 434: the sync of one server that follows an edit of its address, user or libraries.
public sealed class ServerSyncTests : IDisposable
{
    private readonly SyncFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task A_server_sync_reads_only_that_server()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movies(2));
        _fixture.Client.Add(TestLibraries.Shows, RemoteItemType.Series, TestItems.Series("s"));
        var edited = await _fixture.AddServerAsync("A", TestLibraries.Films);
        var other = await _fixture.AddServerAsync("B", TestLibraries.Shows);

        Assert.Equal(SyncStartResult.Started, _fixture.Sync.StartServerSync(edited.Id));
        await _fixture.Sync.WaitIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(_fixture.Index.Snapshot.Find(edited.Id));
        Assert.Null(_fixture.Index.Snapshot.Find(other.Id));
    }

    [Fact]
    public async Task A_sync_in_progress_finishes_then_the_server_sync_follows_it()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movies(2));
        var server = await _fixture.AddServerAsync("A", TestLibraries.Films);
        await _fixture.Sync.RunAsync(CancellationToken.None);
        var reads = _fixture.Client.Queries.Count;
        _fixture.Client.Queries.Clear();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Client.OnPage = (_, count) =>
        {
            if (count == 1)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }

            return RemoteFailure.None;
        };

        Assert.Equal(SyncStartResult.Started, _fixture.Sync.TryStart());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SyncStartResult.AlreadyRunning, _fixture.Sync.StartServerSync(server.Id));
        release.TrySetResult();
        await _fixture.Sync.WaitIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2 * reads, _fixture.Client.Queries.Count);
        Assert.False(_fixture.Sync.GetStatus().IsRunning);
    }

    [Fact]
    public async Task A_server_sync_with_an_unusable_key_reads_nothing_and_is_not_kept_for_later()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movies(2));
        var server = await _fixture.AddServerAsync("A", TestLibraries.Films);
        _fixture.KeySource.Key = null;

        Assert.Equal(SyncStartResult.Suspended, _fixture.Sync.StartServerSync(server.Id));
        Assert.Empty(_fixture.Client.Queries);
        _fixture.KeySource.Key = TestValues.Key;
        await _fixture.Sync.RunIncrementalAsync(CancellationToken.None);

        // A run handed on to a waiting server sync would still show as running.
        Assert.False(_fixture.Sync.GetStatus().IsRunning);
    }
}
