using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

/// <summary>
/// Decision 20 end to end over a real index, resolver, registry and folder: the merge takes the path of any two copies that
/// became one title, and an undo gives every copy a stub again.
/// </summary>
public sealed class ManualMergeStubTests : IDisposable
{
    private static readonly DateTime _yesterday = new(2026, 11, 4, 20, 0, 0, DateTimeKind.Utc);

    private readonly StubFixture _fixture = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly List<string> _calls = [];
    private readonly Mock<IVersionLinker> _linker = new();
    private readonly List<string> _links = [];
    private readonly User _anna = new("anna", "auth", "reset");
    private readonly ManualMergeService _service;

    public ManualMergeStubTests()
    {
        _users.Setup(users => users.GetUsers()).Returns([_anna]);
        _fixture.Library
            .Setup(library => library.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()))
            .Callback<BaseItem, DeleteOptions>((item, _) => _calls.Add("delete " + item.Path));
        _userData
            .Setup(data => data.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), UserDataSaveReason.Import, It.IsAny<CancellationToken>()))
            .Callback<User, BaseItem, UserItemData, UserDataSaveReason, CancellationToken>((_, item, _, _, _) => _calls.Add("save " + item.Path));
        _linker
            .Setup(linker => linker.LinkAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<bool, CancellationToken>((thorough, _) => _links.Add(thorough + " " + _fixture.Registry.State.Groups.Count))
            .Returns(Task.CompletedTask);
        _service = new ManualMergeService(_fixture.Resolver, _fixture.Merges, _fixture.Materializer, _fixture.Sync.Clock, new CapturingLogger<ManualMergeService>(), _linker.Object);
    }

    public void Dispose()
    {
        _fixture.Merges.Dispose();
        _fixture.Dispose();
    }

    private static RemoteItem Film(string id, string name, int height, string tmdb, string imdb) => TestItems.Movie(id, name, 2020, tmdb) with
    {
        ProviderIds = new Dictionary<string, string?> { ["Tmdb"] = tmdb, ["Imdb"] = imdb },
        MediaSources = [TestItems.Source(id, height, height * 5000)]
    };

    // A(1, a), B(1, b), C(2, b): they stay apart, each on a server of its own, or in three libraries of one server.
    private async Task ChainAsync(bool oneServer = false)
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary("films-b", "B", "movies"));
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary("films-c", "C", "movies"));
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, Film("a", "Uno", 720, "1", "tta"));
        _fixture.Sync.Client.Add("films-b", RemoteItemType.Movie, Film("b", "Due", 1080, "1", "ttb"));
        _fixture.Sync.Client.Add("films-c", RemoteItemType.Movie, Film("c", "Tre", 480, "2", "ttb"));
        if (oneServer)
        {
            await _fixture.Sync.AddServerAsync("A", "films", "films-b", "films-c");
        }
        else
        {
            await _fixture.Sync.AddServerAsync("A", "films");
            await _fixture.Sync.AddServerAsync("B", "films-b");
            await _fixture.Sync.AddServerAsync("C", "films-c");
        }

        await _fixture.SyncAndMaterializeAsync();
        Assert.Equal(3, _fixture.Registry.State.Stubs.Count);
    }

    private Task<ManualMergeOutcome> MergeAllAsync()
    {
        var keys = Assert.Single(_service.GetList().Cases).Titles.Select(title => title.WorkKey).ToList();
        return _service.MergeAsync(keys, CancellationToken.None);
    }

    private StubCleanupTask Cleanup() => new(
        _fixture.Registry,
        _fixture.Resolver,
        _fixture.Sync.Index,
        _fixture.Sync.Registry,
        _fixture.Sync.Store,
        _fixture.Library.Object,
        _users.Object,
        _userData.Object,
        Mock.Of<IProviderManager>(),
        Mock.Of<IFileSystem>(),
        _fixture.Remover,
        new CapturingLogger<StubCleanupTask>());

    private Task ScanAsync() => Cleanup().Run(new Progress<double>(), CancellationToken.None);

    private Movie Item(string title)
    {
        var stub = _fixture.Registry.State.Stubs.Single(entry => entry.RelativePath.Contains(title, StringComparison.Ordinal));
        var item = new Movie { Id = Guid.NewGuid(), Path = _fixture.Path_(stub.RelativePath) };
        _fixture.Library.Setup(library => library.FindByPath(item.Path, false)).Returns(item);
        return item;
    }

    private UserItemData Give(BaseItem item, UserItemData data)
    {
        _userData.Setup(manager => manager.GetUserData(_anna, item)).Returns(data);
        return data;
    }

    [Fact]
    public async Task AMergeOnOneServer_KeepsOneStub_WithEveryCopyAsSource_BestFirst()
    {
        await ChainAsync(oneServer: true);
        var lowest = _fixture.Registry.State.Stubs.Single(stub => stub.RelativePath.Contains("Uno", StringComparison.Ordinal));

        Assert.Equal(ManualMergeOutcome.Merged, await MergeAllAsync());

        var survivor = _fixture.Registry.State.Stubs.Single(stub => stub.Id == lowest.Id);
        Assert.Equal(lowest.RelativePath, survivor.RelativePath);
        Assert.Equal(["b", "a", "c"], survivor.Sources.Select(source => source.ItemId));
        var nfo = Path.Combine(Path.GetDirectoryName(_fixture.Path_(survivor.RelativePath))!, "movie.nfo");
        Assert.Contains("Due", await File.ReadAllTextAsync(nfo), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergedSeriesAcrossServers_KeepTheirFolders_AndGroupOnlyTheEpisodesOfOneInterval()
    {
        await SeriesAsync();

        Assert.Equal(ManualMergeOutcome.Merged, await MergeSeriesAsync());

        var state = _fixture.Registry.State;
        Assert.Equal(2, state.Series.Count);
        Assert.Equal(["a1", "a2", "b1", "b3"], state.Stubs.SelectMany(stub => stub.Sources).Select(source => source.ItemId).Order(StringComparer.Ordinal));
        Assert.All(state.Stubs, stub => Assert.Equal(state.Series.Single(series => series.ServerId == stub.ServerId).Id, stub.SeriesStubId));
        var group = Assert.Single(state.Groups);
        Assert.Equal(["a1", "b1"], group.StubIds.Select(id => state.Stubs.Single(stub => stub.Id == id).Sources[0].ItemId).Order(StringComparer.Ordinal));
        Assert.Single(state.Series.Select(series => series.WorkKey).Distinct());
    }

    [Fact]
    public async Task MergedSeriesOnOneServer_KeepOneStubPerEpisode_InTheFolderItWasCreatedIn()
    {
        await SeriesAsync(oneServer: true);
        var before = _fixture.Registry.State.Stubs.ToDictionary(stub => stub.Sources[0].ItemId, stub => stub.SeriesStubId);
        ScanEpisodes();

        Assert.Equal(ManualMergeOutcome.Merged, await MergeSeriesAsync());
        await ScanAsync();

        var state = _fixture.Registry.State;
        Assert.Equal(3, state.Stubs.Count);
        var both = state.Stubs.Single(stub => stub.Sources.Count == 2);
        Assert.Equal(["a1", "b1"], both.Sources.Select(source => source.ItemId).Order(StringComparer.Ordinal));
        Assert.Equal(before[both.Sources.Single(source => before[source.ItemId] == both.SeriesStubId).ItemId], both.SeriesStubId);
        Assert.Equal(before["b3"], state.Stubs.Single(stub => stub.Sources[0].ItemId == "b3").SeriesStubId);
        Assert.Empty(state.Groups);
    }

    [Fact]
    public async Task AnUndoOfMergedSeries_GivesEachSeriesItsEpisodesBack()
    {
        await SeriesAsync(oneServer: true);
        var before = _fixture.Registry.State.Stubs.Select(stub => (stub.Sources[0].ItemId, stub.SeriesStubId)).Order().ToList();
        ScanEpisodes();
        await MergeSeriesAsync();
        await ScanAsync();
        Assert.Equal(3, _fixture.Registry.State.Stubs.Count);

        Assert.True(await _service.UndoAsync(Assert.Single(_fixture.Merges.Merges).Id, CancellationToken.None));
        await ScanAsync();

        Assert.Equal(before, _fixture.Registry.State.Stubs.Select(stub => (stub.Sources[0].ItemId, stub.SeriesStubId)).Order().ToList());
    }

    // Two series of the same title with different IDs: Uno (a1, a2) and Due (b1, b3), on two servers or in two libraries.
    private async Task SeriesAsync(bool oneServer = false)
    {
        RemoteItem Show(string id, string tmdb) => TestItems.Series(id) with { ProviderIds = new Dictionary<string, string?> { ["Tmdb"] = tmdb } };
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary("shows-b", "Shows B", "tvshows"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, Show("sa", "1"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("a1", "sa", 1, 1), TestItems.Episode("a2", "sa", 1, 2));
        _fixture.Sync.Client.Add("shows-b", RemoteItemType.Series, Show("sb", "2"));
        _fixture.Sync.Client.Add("shows-b", RemoteItemType.Episode, TestItems.Episode("b1", "sb", 1, 1) with { MediaSources = [TestItems.Source("b1", 1080, 9_000_000)] }, TestItems.Episode("b3", "sb", 1, 3));
        if (oneServer)
        {
            await _fixture.Sync.AddServerAsync("A", "shows", "shows-b");
        }
        else
        {
            await _fixture.Sync.AddServerAsync("A", "shows");
            await _fixture.Sync.AddServerAsync("B", "shows-b");
        }

        await _fixture.SyncAndMaterializeAsync();
        Assert.Equal(4, _fixture.Registry.State.Stubs.Count);
        Assert.Equal(2, _fixture.Registry.State.Series.Count);
    }

    // Gives each episode stub the item a library scan would make.
    private void ScanEpisodes()
    {
        foreach (var stub in _fixture.Registry.State.Stubs)
        {
            var item = new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), Path = _fixture.Path_(stub.RelativePath) };
            _fixture.Library.Setup(library => library.FindByPath(item.Path, false)).Returns(item);
        }
    }

    private Task<ManualMergeOutcome> MergeSeriesAsync()
    {
        var keys = Assert.Single(_service.GetList().SeriesCases).Titles.Select(title => title.WorkKey).ToList();
        return _service.MergeAsync(keys, CancellationToken.None);
    }

    [Fact]
    public async Task AMergeAcrossServers_KeepsTheStubOfEach_InOneGroup()
    {
        await ChainAsync();
        var before = _fixture.Registry.State.Stubs.Select(stub => stub.Id).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(ManualMergeOutcome.Merged, await MergeAllAsync());
        await ScanAsync();

        Assert.Equal(before, _fixture.Registry.State.Stubs.Select(stub => stub.Id).Order(StringComparer.Ordinal));
        Assert.Equal(before, Assert.Single(_fixture.Registry.State.Groups).StubIds.Order(StringComparer.Ordinal));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task AMergeAcrossServers_LinksTheVersionsAtOnce_WithNoScanToWaitFor()
    {
        await ChainAsync();
        _fixture.Library.Invocations.Clear();

        await MergeAllAsync();

        Assert.Equal(["False 1"], _links);
        _fixture.Library.Verify(library => library.QueueLibraryScan(), Times.Never);
    }

    [Fact]
    public async Task AnUndo_DetachesAtOnceOnlyTheStubsOfTheGroupItDissolved()
    {
        await ChainAsync();
        await MergeAllAsync();
        var merge = Assert.Single(_fixture.Merges.Merges);
        var group = Assert.Single(_fixture.Registry.State.Groups).StubIds.ToList();
        _links.Clear();
        _linker
            .Setup(linker => linker.GroupedStubsOf(It.IsAny<IReadOnlyCollection<MergedCopy>>()))
            .Returns((IReadOnlyCollection<MergedCopy> copies) =>
            {
                _links.Add("grouped " + copies.Count + " " + _fixture.Registry.State.Groups.Count);
                return group;
            });
        _linker
            .Setup(linker => linker.DetachAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<string>, CancellationToken>((stubs, _) => _links.Add("detach " + string.Join(',', stubs.Order(StringComparer.Ordinal)) + " " + _fixture.Registry.State.Groups.Count))
            .Returns(Task.CompletedTask);

        await _service.UndoAsync(merge.Id, CancellationToken.None);

        Assert.Equal(["grouped 3 1", "False 0", "detach " + string.Join(',', group.Order(StringComparer.Ordinal)) + " 0"], _links);
        Assert.Equal(merge.Copies, (IEnumerable<MergedCopy>)_linker.Invocations.Single(call => call.Method.Name == nameof(IVersionLinker.GroupedStubsOf)).Arguments[0]);
    }

    [Fact]
    public async Task AnUndoOfAMergeThatIsGone_DetachesNothing()
    {
        await ChainAsync();
        await MergeAllAsync();
        _linker.Invocations.Clear();

        Assert.False(await _service.UndoAsync(Guid.NewGuid(), CancellationToken.None));

        _linker.Verify(linker => linker.DetachAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AMerge_FindsTheEntitiesBeforeTheLink_AndCarriesTheirStateAfterIt()
    {
        await ChainAsync();
        var order = new List<string>();
        var heads = new List<MergeHead> { new(Guid.NewGuid(), null), new(Guid.NewGuid(), null) };
        IReadOnlyList<MergedCopy>? copies = null;
        var carry = new Mock<IMergeWatchStateCarry>();
        carry.Setup(item => item.FindHeads(It.IsAny<IReadOnlyList<MergedCopy>>()))
            .Callback<IReadOnlyList<MergedCopy>>(found =>
            {
                copies = found;
                order.Add("heads");
            })
            .Returns(heads);
        carry.Setup(item => item.CarryAsync(heads, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("carry"))
            .ReturnsAsync((WatchStateCarryReport?)null);
        _linker.Setup(linker => linker.LinkAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("link"))
            .Returns(Task.CompletedTask);
        var service = new ManualMergeService(_fixture.Resolver, _fixture.Merges, _fixture.Materializer, _fixture.Sync.Clock, new CapturingLogger<ManualMergeService>(), _linker.Object, carry.Object);
        var keys = Assert.Single(service.GetList().Cases).Titles.Select(title => title.WorkKey).ToList();

        Assert.Equal(ManualMergeOutcome.Merged, await service.MergeAsync(keys, CancellationToken.None));

        Assert.Equal(["heads", "link", "carry"], order);
        Assert.Equal(3, copies!.Count);

        order.Clear();
        Assert.True(await service.UndoAsync(Assert.Single(_fixture.Merges.Merges).Id, CancellationToken.None));

        Assert.Equal(["link"], order);
    }

    [Fact]
    public async Task AFailedCarry_KeepsTheMerge_AndIsLoggedByItsTypeOnly()
    {
        await ChainAsync();
        var carry = new Mock<IMergeWatchStateCarry>();
        carry.Setup(item => item.FindHeads(It.IsAny<IReadOnlyList<MergedCopy>>())).Returns([new(Guid.NewGuid(), null), new(Guid.NewGuid(), null)]);
        carry.Setup(item => item.CarryAsync(It.IsAny<IReadOnlyList<MergeHead>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("no space on /secret"));
        var logger = new CapturingLogger<ManualMergeService>();
        var service = new ManualMergeService(_fixture.Resolver, _fixture.Merges, _fixture.Materializer, _fixture.Sync.Clock, logger, _linker.Object, carry.Object);
        var keys = Assert.Single(service.GetList().Cases).Titles.Select(title => title.WorkKey).ToList();

        Assert.Equal(ManualMergeOutcome.Merged, await service.MergeAsync(keys, CancellationToken.None));

        Assert.Single(_fixture.Merges.Merges);
        Assert.Contains(logger.Lines, line => line.Contains(nameof(IOException), StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, line => line.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedLink_KeepsTheMerge_AndIsLoggedByItsTypeOnly()
    {
        await ChainAsync();
        var linker = new Mock<IVersionLinker>();
        linker.Setup(item => item.LinkAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("no space on http://secret"));
        var logger = new CapturingLogger<ManualMergeService>();
        var service = new ManualMergeService(_fixture.Resolver, _fixture.Merges, _fixture.Materializer, _fixture.Sync.Clock, logger, linker.Object);
        var keys = Assert.Single(service.GetList().Cases).Titles.Select(title => title.WorkKey).ToList();

        Assert.Equal(ManualMergeOutcome.Merged, await service.MergeAsync(keys, CancellationToken.None));

        Assert.Single(_fixture.Merges.Merges);
        Assert.Contains(logger.Lines, line => line.Contains(nameof(IOException), StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, line => line.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMerge_QueuesTheScanThatHandsTheStateOver()
    {
        await ChainAsync(oneServer: true);
        _fixture.Library.Invocations.Clear();

        await MergeAllAsync();

        _fixture.Library.Verify(library => library.QueueLibraryScan(), Times.Once);
    }

    [Fact]
    public async Task TheScanHandsTheStateOverAndRemovesTheOtherStubs()
    {
        await ChainAsync(oneServer: true);
        var (uno, due, tre) = (Item("Uno"), Item("Due"), Item("Tre"));
        var survivor = Give(uno, new UserItemData { Key = "u" });
        Give(due, new UserItemData { Key = "d", Played = true, PlayCount = 2, LastPlayedDate = _yesterday });
        Give(tre, new UserItemData { Key = "t", IsFavorite = true });

        await MergeAllAsync();
        await ScanAsync();

        Assert.True(survivor.Played);
        Assert.Equal(2, survivor.PlayCount);
        Assert.True(survivor.IsFavorite);
        Assert.Single(_fixture.Registry.State.Stubs);
        Assert.True(File.Exists(uno.Path));
        Assert.False(File.Exists(due.Path));
        Assert.False(File.Exists(tre.Path));
        Assert.Equal(["save " + uno.Path, "delete " + due.Path, "save " + uno.Path, "delete " + tre.Path], _calls);
    }

    [Fact]
    public async Task AnUndo_GivesTheCopiesStubsAgain_AndTheSurvivorKeepsItsState()
    {
        await ChainAsync(oneServer: true);
        var (uno, due, tre) = (Item("Uno"), Item("Due"), Item("Tre"));
        var survivor = Give(uno, new UserItemData { Key = "u" });
        Give(due, new UserItemData { Key = "d", Played = true, PlayCount = 2, LastPlayedDate = _yesterday });
        Give(tre, new UserItemData { Key = "t" });
        await MergeAllAsync();
        await ScanAsync();
        var kept = Assert.Single(_fixture.Registry.State.Stubs);
        var merge = Assert.Single(_service.GetList().Merges);
        _calls.Clear();

        Assert.True(await _service.UndoAsync(merge.Id, CancellationToken.None));
        await ScanAsync();

        var stubs = _fixture.Registry.State.Stubs;
        Assert.Equal(3, stubs.Count);
        Assert.Contains(stubs, stub => stub.Id == kept.Id && stub.RelativePath == kept.RelativePath && stub.Sources.Single().ItemId == "a");
        Assert.Equal(["a", "b", "c"], stubs.SelectMany(stub => stub.Sources).Select(source => source.ItemId).Order(StringComparer.Ordinal));
        Assert.Empty(_calls);
        Assert.True(survivor.Played);
        Assert.True(File.Exists(uno.Path));
        Assert.All(stubs, stub => Assert.True(File.Exists(_fixture.Path_(stub.RelativePath))));
        Assert.Equal(3, _service.GetList().Cases.Single().Titles.Count);
    }

    [Fact]
    public async Task AnUndoBeforeTheScan_LeavesEveryStubInPlace()
    {
        await ChainAsync();
        var before = _fixture.Registry.State.Stubs.Select(stub => stub.Id).Order(StringComparer.Ordinal).ToList();

        await MergeAllAsync();
        await _service.UndoAsync(Assert.Single(_service.GetList().Merges).Id, CancellationToken.None);

        Assert.Equal(before, _fixture.Registry.State.Stubs.Select(stub => stub.Id).Order(StringComparer.Ordinal));
        Assert.Equal(1, _fixture.Registry.State.Stubs.Count(stub => stub.Sources.Single().ItemId == "b"));
    }

    [Fact]
    public async Task AnUndoOfAnUnknownMerge_IsReported()
    {
        Assert.False(await _service.UndoAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task TheMergeSurvivesASync()
    {
        await ChainAsync();
        await MergeAllAsync();

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(MatchKind.Manual, Assert.Single(_fixture.Resolver.Resolve().Contents).Identity.Match);
    }

    [Fact]
    public async Task MergingAPartOfAChain_ThenTheRest_NeedsOneUndo()
    {
        await ChainAsync();
        var titles = Assert.Single(_service.GetList().Cases).Titles;
        await _service.MergeAsync([titles[0].WorkKey, titles[1].WorkKey], CancellationToken.None);
        var rest = Assert.Single(_service.GetList().Cases).Titles;
        Assert.Equal(2, rest.Count);
        Assert.Single(rest[0].MergeIds);

        await _service.MergeAsync(rest.Select(title => title.WorkKey).ToList(), CancellationToken.None);

        var merge = Assert.Single(_service.GetList().Merges);
        Assert.Equal(3, merge.Copies.Count);
        Assert.True(merge.IsInEffect);
        Assert.Empty(_service.GetList().Cases);

        await _service.UndoAsync(merge.Id, CancellationToken.None);

        Assert.Equal(3, Assert.Single(_service.GetList().Cases).Titles.Count);
    }

    [Fact]
    public async Task ALocalCopyMerged_LeavesTheStubAndItsState_AndAnUndoChangesNothingOnDisk()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, Film("r", "Remoto", 1080, "1", "tt1"));
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var stub = Item("Remoto");
        Give(stub, new UserItemData { Key = "s", Played = true, LastPlayedDate = _yesterday });

        var local = new Movie { Id = Guid.NewGuid(), Path = "/media/film/Locale.mkv" };
        _fixture.Library.Setup(library => library.GetItemById(local.Id)).Returns(local);
        var localState = Give(local, new UserItemData { Key = "l" });
        _fixture.LocalCatalog.Items.Add(new CatalogItem { ServerId = "local", ItemId = local.Id.ToString("N"), Kind = ContentKind.Movie, Title = "Remoto", Year = 2020, Ids = new ContentIds("9") });

        Assert.Equal(ManualMergeOutcome.Merged, await MergeAllAsync());
        await ScanAsync();

        Assert.False(localState.Played);
        Assert.Single(_fixture.Registry.State.Stubs);
        Assert.True(File.Exists(stub.Path));
        Assert.Empty(_calls);
        Assert.Equal([local.Id], Assert.Single(_fixture.Registry.State.Groups).LocalItemIds);

        var copies = Assert.Single(_service.GetList().Merges).Copies;
        Assert.Single(copies, copy => copy.Copy.IsLocal);

        await _service.UndoAsync(Assert.Single(_service.GetList().Merges).Id, CancellationToken.None);

        Assert.Single(_fixture.Registry.State.Stubs);
        Assert.True(File.Exists(stub.Path));
    }

    [Fact]
    public async Task TheCopiesNameTheirServerAndNoAddress()
    {
        await ChainAsync();

        var list = _service.GetList();

        var titles = Assert.Single(list.Cases).Titles;
        Assert.Equal(["A", "B", "C"], titles.Select(title => title.Copies.Single().ServerName));
        Assert.DoesNotContain(TestValues.Host, System.Text.Json.JsonSerializer.Serialize(list), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemovedServer_LeavesItsCopyNamedAndNotFound()
    {
        await ChainAsync();
        await MergeAllAsync();
        var server = _fixture.Sync.Registry.GetServers().Single(entry => entry.Name == "C");

        _fixture.Sync.Registry.Remove(server.Id);

        var copy = Assert.Single(Assert.Single(_service.GetList().Merges).Copies, entry => entry.Copy.Title == "Tre");
        Assert.False(copy.IsFound);
        Assert.Null(copy.ServerName);
    }

    [Fact]
    public async Task RefusesFewerThanTwoTitles()
    {
        await ChainAsync();
        var key = Assert.Single(_service.GetList().Cases).Titles[0].WorkKey;

        Assert.Equal(ManualMergeOutcome.TooFewTitles, await _service.MergeAsync([key, key], CancellationToken.None));
        Assert.Empty(_fixture.Merges.Merges);
    }

    [Fact]
    public async Task RefusesTitlesThatAreNoLongerACase()
    {
        await ChainAsync();
        var keys = Assert.Single(_service.GetList().Cases).Titles.Select(title => title.WorkKey).ToList();

        Assert.Equal(ManualMergeOutcome.ListChanged, await _service.MergeAsync([keys[0], "movie|tmdb:999"], CancellationToken.None));
        Assert.Empty(_fixture.Merges.Merges);
    }

    [Fact]
    public async Task RefusesTwoTitlesWithALocalCopy()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, Film("r", "Doppio", 1080, "3", "tt3"));
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        _fixture.LocalCatalog.Items.Add(new CatalogItem { ServerId = "local", ItemId = "l1", Kind = ContentKind.Movie, Title = "Doppio", Year = 2020, Ids = new ContentIds("1") });
        _fixture.LocalCatalog.Items.Add(new CatalogItem { ServerId = "local", ItemId = "l2", Kind = ContentKind.Movie, Title = "Doppio", Year = 2020, Ids = new ContentIds("2") });

        Assert.Equal(ManualMergeOutcome.TwoLocalTitles, await MergeAllAsync());
        Assert.Empty(_fixture.Merges.Merges);
    }

    [Fact]
    public async Task AFailureToApply_KeepsTheChoice()
    {
        await ChainAsync();
        var materializer = new Mock<ILibraryMaterializer>();
        materializer.Setup(item => item.MaterializeAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("no space on http://secret"));
        var logger = new CapturingLogger<ManualMergeService>();
        var service = new ManualMergeService(_fixture.Resolver, _fixture.Merges, materializer.Object, _fixture.Sync.Clock, logger);
        var keys = Assert.Single(service.GetList().Cases).Titles.Select(title => title.WorkKey).ToList();

        Assert.Equal(ManualMergeOutcome.Merged, await service.MergeAsync(keys, CancellationToken.None));

        Assert.Single(_fixture.Merges.Merges);
        Assert.DoesNotContain(logger.Lines, line => line.Contains("secret", StringComparison.Ordinal));
    }
}
