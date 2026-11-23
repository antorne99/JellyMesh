using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

// A database that comes back from its copy lacks what changed after it, so a read of changes does not go on from where it was.
public sealed class ReopenedDatabaseSyncTests : IDisposable
{
    private readonly SyncFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private FakeRemoteClient Remote => _fixture.Client;

    private Task RunFull() => _fixture.Sync.RunAsync(CancellationToken.None);

    private Task RunIncremental() => _fixture.Sync.RunIncrementalAsync(CancellationToken.None);

    private string[] IdsOf(RemoteServerInfo server)
        => _fixture.Index.Snapshot.Find(server.Id)!.Items.Select(item => item.ItemId).Order().ToArray();

    private void Tick()
    {
        _fixture.Clock.Now = _fixture.Clock.Now.AddMinutes(15);
        Remote.ServerTime = Remote.ServerTime?.AddMinutes(15);
    }

    // The film of the last change, which the copy of the database does not hold.
    private void ChangeOnRemote(string id)
    {
        Remote.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie(id));
        Remote.ItemsById.Add(TestItems.Movie(id));
        Remote.SavedUtc[id] = Remote.ServerTime!.Value;
    }

    private async Task RestoreCopyWithoutAsync(string id)
    {
        await _fixture.Database.ExecuteAsync("DELETE FROM index_items WHERE item_id = '" + id + "';");
        _fixture.Database.Reopen();
    }

    private Task RestoreCopyDuringTheNextReadAsync(string id, Func<Task>? then = null)
    {
        Remote.BeforeClock = async (_, _) =>
        {
            Remote.BeforeClock = (_, _) => Task.CompletedTask;
            await RestoreCopyWithoutAsync(id);
            await (then?.Invoke() ?? Task.CompletedTask);
        };
        return RunIncremental();
    }

    // Two films and a change read after the first full read: by its feed from a peer, by polling from a stock server.
    private async Task<RemoteServerInfo> ServerWithAChangeReadAsync(bool peer)
    {
        Remote.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("a"), TestItems.Movie("b"));
        if (peer)
        {
            Remote.Capabilities = new RemoteCapabilities(RemoteMode.JellyMesh, 1);
            Remote.Feed = cursor => RemoteResult.Success(new RemoteChangePage(
                cursor is null,
                cursor is null ? "cursor-0" : "cursor-1",
                false,
                cursor == "cursor-0" ? [new RemoteChange("c", "Movie", [TestLibraries.Films])] : []));
        }

        var server = await _fixture.AddServerAsync("A", TestLibraries.Films);
        await RunFull();
        Tick();
        ChangeOnRemote("c");
        await RunIncremental();
        Assert.Equal(["a", "b", "c"], IdsOf(server));

        // The mark moves on, past the overlap of the next read.
        Tick();
        await RunIncremental();
        Tick();
        Remote.Queries.Clear();
        Remote.FeedReads.Clear();
        return server;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AChangeReadAfterTheCopyIsReadAgain(bool peer)
    {
        var server = await ServerWithAChangeReadAsync(peer);
        await RestoreCopyWithoutAsync("c");

        await RunIncremental();

        Assert.Equal(["a", "b", "c"], IdsOf(server));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARestoreDuringAReadOfChangesReadsThemAgain(bool peer)
    {
        var server = await ServerWithAChangeReadAsync(peer);

        await RestoreCopyDuringTheNextReadAsync("c");
        Tick();
        await RunIncremental();

        Assert.Equal(["a", "b", "c"], IdsOf(server));
    }

    // Another save during the read takes the restored rows into memory before the changes arrive.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARestoreTakenIntoMemoryDuringAReadOfChangesReadsThemAgain(bool peer)
    {
        var server = await ServerWithAChangeReadAsync(peer);

        await RestoreCopyDuringTheNextReadAsync(
            "c",
            () => _fixture.Index.MarkServerFailedAsync(Guid.NewGuid(), RemoteFailure.Unreachable, _fixture.Clock.Now, CancellationToken.None));
        Tick();
        await RunIncremental();

        Assert.Equal(["a", "b", "c"], IdsOf(server));
    }

    // Another server's save took the restored rows into memory: only the place in the feed is of the older opening.
    [Fact]
    public async Task ARestoreAlreadyTakenIntoMemoryDoesNotGoOnFromTheOldPlaceInTheFeed()
    {
        var server = await ServerWithAChangeReadAsync(peer: true);
        await RestoreCopyWithoutAsync("c");
        await _fixture.Index.MarkServerFailedAsync(Guid.NewGuid(), RemoteFailure.Unreachable, _fixture.Clock.Now, CancellationToken.None);

        await RunIncremental();

        Assert.Equal(["a", "b", "c"], IdsOf(server));
        Assert.DoesNotContain("cursor-1", Remote.FeedReads);
    }

    // The place of a full read that a restore overtook is not one to go on from either.
    [Fact]
    public async Task ARestoreDuringAFullReadOfAPeerReadsItAgain()
    {
        await ServerWithAChangeReadAsync(peer: true);
        Remote.BeforeClock = (_, _) =>
        {
            Remote.BeforeClock = (_, _) => Task.CompletedTask;
            _fixture.Database.Reopen();
            return Task.CompletedTask;
        };
        await RunFull();
        Remote.FeedReads.Clear();

        await RunIncremental();

        Assert.Null(Assert.Single(Remote.FeedReads));
    }

    // Once everything was read again, the reads of changes go on as before.
    [Fact]
    public async Task AfterTheFullReadTheFeedGoesOn()
    {
        await ServerWithAChangeReadAsync(peer: true);
        await RestoreCopyWithoutAsync("c");
        await RunIncremental();
        Remote.FeedReads.Clear();
        Remote.Queries.Clear();

        await RunIncremental();
        await RunIncremental();

        Assert.Equal(["cursor-0", "cursor-1"], Remote.FeedReads);
    }
}
