using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

public sealed class IncrementalSyncTests : IDisposable
{
    private readonly SyncFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private FakeRemoteClient Remote => _fixture.Client;

    private ServerCatalog CatalogOf(RemoteServerInfo server) => _fixture.Index.Snapshot.Find(server.Id)!;

    private string[] IdsOf(RemoteServerInfo server) => CatalogOf(server).Items.Select(item => item.ItemId).Order().ToArray();

    private Task RunFull() => _fixture.Sync.RunAsync(CancellationToken.None);

    private Task RunIncremental() => _fixture.Sync.RunIncrementalAsync(CancellationToken.None);

    /// <summary>A server with two films, read in full, with the clocks moved on 15 minutes.</summary>
    private async Task<RemoteServerInfo> SyncedServerAsync()
    {
        Remote.Add("films", RemoteItemType.Movie, TestItems.Movie("a", "Alfa"), TestItems.Movie("b", "Beta"));
        var server = await _fixture.AddServerAsync("A", "films");
        await RunFull();
        Tick();
        return server;
    }

    private void Tick()
    {
        _fixture.Clock.Now = _fixture.Clock.Now.AddMinutes(15);
        Remote.ServerTime = Remote.ServerTime?.AddMinutes(15);
        Remote.Queries.Clear();
    }

    private void SaveOnRemote(params string[] ids)
    {
        foreach (var id in ids)
        {
            Remote.SavedUtc[id] = Remote.ServerTime!.Value;
        }
    }

    [Fact]
    public async Task AFullReadRecordsWhereTheNextReadStarts()
    {
        var server = await SyncedServerAsync();

        var mark = CatalogOf(server).Mark!;

        Assert.Equal(SyncFixture.Start, mark.ReadStartedUtc);
        Assert.Equal(["films"], mark.LibraryIds);
    }

    [Fact]
    public async Task ANewItemIsCaught()
    {
        var server = await SyncedServerAsync();
        Remote.Add("films", RemoteItemType.Movie, TestItems.Movie("c", "Gamma"));
        SaveOnRemote("c");

        var run = await _fixture.Sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(SyncStartResult.Started, run);
        Assert.Equal(["a", "b", "c"], IdsOf(server));
        Assert.All(Remote.Queries, query => Assert.NotNull(query.ChangedSinceUtc));
    }

    [Fact]
    public async Task AChangedItemIsUpdatedAndTheOthersAreNotTouched()
    {
        var server = await SyncedServerAsync();
        var untouched = CatalogOf(server).Items.Single(item => item.ItemId == "b");
        var list = Remote.Items[("films", RemoteItemType.Movie)];
        list[0] = TestItems.Movie("a", "Alfa renamed");
        SaveOnRemote("a");

        await RunIncremental();

        var catalog = CatalogOf(server);
        Assert.Equal("Alfa renamed", catalog.Items.Single(item => item.ItemId == "a").Name);
        Assert.Equal(_fixture.Clock.Now, catalog.Items.Single(item => item.ItemId == "a").LastSeenUtc);
        Assert.Same(untouched, catalog.Items.Single(item => item.ItemId == "b"));
    }

    [Fact]
    public async Task AChangeJustBeforeTheLastReadBeganIsStillCaught()
    {
        var server = await SyncedServerAsync();
        Remote.Add("films", RemoteItemType.Movie, TestItems.Movie("late"));
        Remote.SavedUtc["late"] = SyncFixture.Start - CatalogSyncService.ChangeOverlap + TimeSpan.FromSeconds(1);

        await RunIncremental();

        Assert.Contains("late", IdsOf(server));
    }

    [Fact]
    public async Task TheReadUsesTheClockOfTheRemoteNotTheLocalOne()
    {
        Remote.ServerTime = SyncFixture.Start.AddHours(-3);
        var server = await SyncedServerAsync();

        await RunIncremental();

        Assert.Equal(SyncFixture.Start.AddHours(-3) - CatalogSyncService.ChangeOverlap, Remote.Queries[0].ChangedSinceUtc);
        Assert.Equal(SyncFixture.Start.AddHours(-3).AddMinutes(15), CatalogOf(server).Mark!.ReadStartedUtc);
    }

    [Fact]
    public async Task EachReadStartsFromThePreviousOne()
    {
        await SyncedServerAsync();
        await RunIncremental();
        var second = _fixture.Clock.Now;
        Tick();

        await RunIncremental();

        Assert.Equal(second - CatalogSyncService.ChangeOverlap, Remote.Queries[0].ChangedSinceUtc);
    }

    [Fact]
    public async Task WithoutADateHeaderTheLocalClockIsUsed()
    {
        Remote.ServerTime = null;
        Remote.Add("films", RemoteItemType.Movie, TestItems.Movie("a"));
        var server = await _fixture.AddServerAsync("A", "films");

        await RunFull();

        Assert.Equal(SyncFixture.Start, CatalogOf(server).Mark!.ReadStartedUtc);
    }

    [Fact]
    public async Task AnIncrementalReadKeepsTheServerAuthoritativeAndTheLastFullReadTime()
    {
        var server = await SyncedServerAsync();

        await RunIncremental();

        var catalog = CatalogOf(server);
        Assert.Equal(ServerSyncState.Complete, catalog.State);
        Assert.True(_fixture.Index.Snapshot.IsAuthoritativeFor(server.Id));
        Assert.Equal(SyncFixture.Start, catalog.LastSuccessUtc);
        Assert.Equal(_fixture.Clock.Now, catalog.LastAttemptUtc);
    }

    [Fact]
    public async Task AnItemGoneFromTheRemoteStaysUntilACompleteRead()
    {
        var server = await SyncedServerAsync();
        Remote.Items[("films", RemoteItemType.Movie)].RemoveAll(item => item.Id == "a");

        await RunIncremental();
        Assert.Equal(["a", "b"], IdsOf(server));

        await RunFull();
        Assert.Equal(["b"], IdsOf(server));
    }

    [Fact]
    public async Task AnItemGoneFromTheRemoteStaysIfTheReconciliationFailsHalfway()
    {
        Remote.Add("films", RemoteItemType.Movie, TestItems.Movies(RemoteCatalogReader.PageSize + 2));
        var server = await _fixture.AddServerAsync("A", "films");
        await RunFull();
        var before = CatalogOf(server).Items;
        Remote.Items[("films", RemoteItemType.Movie)].RemoveAt(0);
        Remote.OnPage = (query, _) => query.StartIndex > 0 ? RemoteFailure.Unreachable : RemoteFailure.None;

        await RunFull();

        Assert.Same(before, CatalogOf(server).Items);
        Assert.False(_fixture.Index.Snapshot.IsAuthoritativeFor(server.Id));
    }

    [Fact]
    public async Task AServerNeverReadGetsAFullRead()
    {
        Remote.Add("films", RemoteItemType.Movie, TestItems.Movie("a"));
        var server = await _fixture.AddServerAsync("A", "films");

        await RunIncremental();

        Assert.Single(Remote.Queries);
        Assert.Null(Remote.Queries[0].ChangedSinceUtc);
        Assert.True(_fixture.Index.Snapshot.IsAuthoritativeFor(server.Id));
    }

    [Fact]
    public async Task ACatalogWithoutAMarkGetsAFullRead()
    {
        var server = await SyncedServerAsync();
        await _fixture.Index.ReplaceServerAsync(server.Id, CatalogOf(server).Items, _fixture.Clock.Now, null, CancellationToken.None);

        await RunIncremental();

        Assert.All(Remote.Queries, query => Assert.Null(query.ChangedSinceUtc));
        Assert.NotNull(CatalogOf(server).Mark);
    }

    [Fact]
    public async Task ANewlyChosenLibraryGetsAFullRead()
    {
        var server = await SyncedServerAsync();
        Remote.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Registry.SetImportedLibraries(server.Id, ["films", "shows"]);

        await RunIncremental();

        Assert.Contains("s", IdsOf(server));
        Assert.All(Remote.Queries, query => Assert.Null(query.ChangedSinceUtc));
    }

    [Fact]
    public async Task ALibraryNoLongerChosenLeavesWithAFullRead()
    {
        Remote.Add("films", RemoteItemType.Movie, TestItems.Movie("a"));
        Remote.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        var server = await _fixture.AddServerAsync("A", "films", "shows");
        await RunFull();
        _fixture.Registry.SetImportedLibraries(server.Id, ["films"]);

        await RunIncremental();

        Assert.Equal(["a"], IdsOf(server));
    }

    [Fact]
    public async Task TheSameLibrariesInAnotherOrderAreNotAChange()
    {
        Remote.Add("films", RemoteItemType.Movie, TestItems.Movie("a"));
        var server = await _fixture.AddServerAsync("A", "films", "shows");
        await RunFull();
        Tick();
        _fixture.Registry.SetImportedLibraries(server.Id, ["shows", "films"]);

        await RunIncremental();

        Assert.All(Remote.Queries, query => Assert.NotNull(query.ChangedSinceUtc));
    }

    [Fact]
    public async Task ANewEpisodeOfAKnownSeriesDoesNotLookTheSeriesUp()
    {
        Remote.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        var server = await _fixture.AddServerAsync("A", "shows");
        await RunFull();
        Tick();
        Remote.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 1));
        SaveOnRemote("e");

        await RunIncremental();

        Assert.Contains("e", IdsOf(server));
        Assert.DoesNotContain(Remote.Queries, query => query.Ids is not null);
    }

    [Fact]
    public async Task AnEpisodeOfAnUnknownSeriesLooksItUp()
    {
        Remote.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        var server = await _fixture.AddServerAsync("A", "shows");
        await RunFull();
        Tick();
        Remote.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "hidden-row", 1, 1));
        Remote.ItemsById.Add(TestItems.Series("hidden-row"));
        SaveOnRemote("e");

        await RunIncremental();

        Assert.Equal(["e", "hidden-row", "s"], IdsOf(server));
    }

    [Theory]
    [InlineData(RemoteFailure.Unreachable)]
    [InlineData(RemoteFailure.Timeout)]
    public async Task AServerGoingOfflineKeepsItsEntriesAndIsMarked(RemoteFailure failure)
    {
        var server = await SyncedServerAsync();
        var before = CatalogOf(server).Items;
        Remote.ClockFailure = failure;

        await RunIncremental();

        var catalog = CatalogOf(server);
        Assert.Same(before, catalog.Items);
        Assert.Equal(ServerSyncState.Stale, catalog.State);
        Assert.False(_fixture.Index.Snapshot.IsAuthoritativeFor(server.Id));
        Assert.True(Assert.Single(_fixture.Sync.GetStatus().Servers).IsOffline);
    }

    [Fact]
    public async Task AServerFailingInTheMiddleOfTheChangesKeepsItsEntries()
    {
        var server = await SyncedServerAsync();
        var before = CatalogOf(server).Items;
        Remote.Add("films", RemoteItemType.Movie, TestItems.Movie("c"));
        SaveOnRemote("c");
        Remote.OnPage = (_, _) => RemoteFailure.Timeout;

        await RunIncremental();

        Assert.Same(before, CatalogOf(server).Items);
        Assert.False(_fixture.Index.Snapshot.IsAuthoritativeFor(server.Id));
    }

    [Fact]
    public async Task AnOfflineServerStaysUnauthoritativeHoweverManyPollsFail()
    {
        var server = await SyncedServerAsync();
        Remote.ClockFailure = RemoteFailure.Unreachable;

        for (var poll = 0; poll < 3; poll++)
        {
            Tick();
            await RunIncremental();
            await RunFull();
        }

        Assert.Equal(["a", "b"], IdsOf(server));
        Assert.False(_fixture.Index.Snapshot.IsAuthoritativeFor(server.Id));
        Assert.Empty(Remote.Queries);
    }

    [Fact]
    public async Task AServerComingBackGetsAFullReadAndRecovers()
    {
        var server = await SyncedServerAsync();
        Remote.ClockFailure = RemoteFailure.Unreachable;
        await RunIncremental();
        Tick();

        // Changed while it was offline: a deletion only a full read can see, and a new item.
        Remote.ClockFailure = RemoteFailure.None;
        Remote.Items[("films", RemoteItemType.Movie)].RemoveAll(item => item.Id == "a");
        Remote.Add("films", RemoteItemType.Movie, TestItems.Movie("c"));
        await RunIncremental();

        Assert.All(Remote.Queries, query => Assert.Null(query.ChangedSinceUtc));
        Assert.Equal(["b", "c"], IdsOf(server));
        var catalog = CatalogOf(server);
        Assert.Equal(ServerSyncState.Complete, catalog.State);
        Assert.Null(catalog.LastFailure);
        Assert.True(_fixture.Index.Snapshot.IsAuthoritativeFor(server.Id));
        Assert.False(Assert.Single(_fixture.Sync.GetStatus().Servers).IsOffline);
    }

    [Fact]
    public async Task AFailureThatIsNotTheNetworkIsNotOffline()
    {
        await SyncedServerAsync();
        Remote.OnPage = (_, _) => RemoteFailure.ServerError;

        await RunIncremental();

        var report = Assert.Single(_fixture.Sync.GetStatus().Servers);
        Assert.False(report.IsOffline);
        Assert.Equal(ServerSyncState.Stale, report.State);
    }

    [Fact]
    public async Task OneServerOfflineDoesNotStopTheNext()
    {
        var bad = await _fixture.AddServerAsync("Bad", "deleted");
        var good = await SyncedServerAsync();
        Remote.Add("films", RemoteItemType.Movie, TestItems.Movie("c"));
        SaveOnRemote("c");

        await RunIncremental();

        Assert.False(_fixture.Index.Snapshot.IsAuthoritativeFor(bad.Id));
        Assert.Contains("c", IdsOf(good));
    }

    [Fact]
    public async Task AnIncrementalSyncIsRefusedWhileAnotherSyncRuns()
    {
        Remote.Add("films", RemoteItemType.Movie, TestItems.Movie("a"));
        await _fixture.AddServerAsync("A", "films");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Remote.OnPage = (_, _) =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return RemoteFailure.None;
        };
        Assert.Equal(SyncStartResult.Started, _fixture.Sync.TryStart());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SyncStartResult.AlreadyRunning, await _fixture.Sync.RunIncrementalAsync(CancellationToken.None));

        release.SetResult();
    }

    [Fact]
    public async Task AnIncrementalThatCannotBeSavedIsReported()
    {
        await SyncedServerAsync();
        var folder = _fixture.Folder;
        System.IO.Directory.Delete(folder, recursive: true);
        await System.IO.File.WriteAllTextAsync(folder, "a file where the folder should be");

        await RunIncremental();

        Assert.True(_fixture.Sync.GetStatus().IndexSaveFailed);
        System.IO.File.Delete(folder);
    }
}
