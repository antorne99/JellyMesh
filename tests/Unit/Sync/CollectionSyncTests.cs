using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

public sealed class CollectionSyncTests : IDisposable
{
    private readonly SyncFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private FakeRemoteClient Remote => _fixture.Client;

    private IndexedBoxSet? CollectionOf(RemoteServerInfo server, string itemId)
        => _fixture.Index.Snapshot.Find(server.Id)!.Items.Single(item => item.ItemId == itemId).Collection;

    private async Task<RemoteServerInfo> ServerWithSagaAsync()
    {
        _fixture.Store.Configuration.FederateCollections = true;
        Remote.Add("films", RemoteItemType.Movie, TestItems.Movie("a", "Alfa"), TestItems.Movie("b", "Beta", tmdb: "2"));
        Remote.AddCollection("films", "box", "Saga", "a");
        return await _fixture.AddServerAsync("A", "films");
    }

    private void Tick()
    {
        _fixture.Clock.Now = _fixture.Clock.Now.AddMinutes(15);
        Remote.ServerTime = Remote.ServerTime?.AddMinutes(15);
    }

    [Fact]
    public async Task AFullReadGivesEachFilmItsCollection()
    {
        var server = await ServerWithSagaAsync();

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal(new IndexedBoxSet("Saga", null), CollectionOf(server, "a"));
        Assert.Null(CollectionOf(server, "b"));
    }

    [Fact]
    public async Task WithTheOptionOffNoCollectionIsRead()
    {
        var server = await ServerWithSagaAsync();
        _fixture.Store.Configuration.FederateCollections = false;

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Empty(Remote.CollectionReads);
        Assert.Null(CollectionOf(server, "a"));
    }

    [Fact]
    public async Task AJellyMeshServerGivesItsCollectionsByTheProviderApiOnAFullRead()
    {
        var server = await ServerWithSagaAsync();
        Remote.Capabilities = new RemoteCapabilities(RemoteMode.JellyMesh, 1);
        Remote.SharedCollections.Add(("films", new RemoteCollectionEntry("b", "Peer saga", "7")));

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal(new IndexedBoxSet("Peer saga", "7"), CollectionOf(server, "b"));
        Assert.Null(CollectionOf(server, "a"));
        Assert.Equal(["peer:films@0"], Remote.CollectionReads);
    }

    [Fact]
    public async Task AFailedCollectionReadOfAJellyMeshServerKeepsTheCollectionsKnown()
    {
        var server = await ServerWithSagaAsync();
        Remote.Capabilities = new RemoteCapabilities(RemoteMode.JellyMesh, 1);
        Remote.SharedCollections.Add(("films", new RemoteCollectionEntry("b", "Peer saga", null)));
        await _fixture.Sync.RunAsync(CancellationToken.None);
        Remote.CollectionFailure = RemoteFailure.ServerError;

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal(ServerSyncState.Complete, _fixture.Index.Snapshot.Find(server.Id)!.State);
        Assert.Equal("Peer saga", CollectionOf(server, "b")!.Name);
        Assert.DoesNotContain(_fixture.Logger.Lines, line => line.Contains(TestValues.Host, StringComparison.Ordinal) || line.Contains(TestValues.Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARenamedCollectionIsTakenByTheNextFullRead()
    {
        var server = await ServerWithSagaAsync();
        await _fixture.Sync.RunAsync(CancellationToken.None);
        Remote.Collections.Clear();
        Remote.AddCollection("films", "box", "Saga renamed", "a");

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal("Saga renamed", CollectionOf(server, "a")!.Name);
    }

    [Fact]
    public async Task AFailedCollectionReadKeepsTheCatalogAndTheCollectionsKnown()
    {
        var server = await ServerWithSagaAsync();
        await _fixture.Sync.RunAsync(CancellationToken.None);
        Remote.CollectionFailure = RemoteFailure.Timeout;
        Remote.Items[("films", RemoteItemType.Movie)][0] = TestItems.Movie("a", "Alfa renamed");

        await _fixture.Sync.RunAsync(CancellationToken.None);

        var catalog = _fixture.Index.Snapshot.Find(server.Id)!;
        Assert.Equal(ServerSyncState.Complete, catalog.State);
        Assert.Equal("Alfa renamed", catalog.Items.Single(item => item.ItemId == "a").Name);
        Assert.Equal("Saga", CollectionOf(server, "a")!.Name);
    }

    [Fact]
    public async Task AReadOfChangesKeepsTheCollectionOfAChangedFilm()
    {
        var server = await ServerWithSagaAsync();
        await _fixture.Sync.RunAsync(CancellationToken.None);
        Tick();
        Remote.Items[("films", RemoteItemType.Movie)][0] = TestItems.Movie("a", "Alfa renamed");
        Remote.SavedUtc["a"] = Remote.ServerTime!.Value;
        Remote.CollectionReads.Clear();

        await _fixture.Sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal("Alfa renamed", _fixture.Index.Snapshot.Find(server.Id)!.Items.Single(item => item.ItemId == "a").Name);
        Assert.Equal("Saga", CollectionOf(server, "a")!.Name);
        Assert.Empty(Remote.CollectionReads);
    }
}
