using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

/// <summary>One title of a server in two remote libraries, as the local libraries hold their folders (§10 rows 439 and 507).</summary>
public sealed class SplitTitleTests : IDisposable
{
    private static readonly DateTime _day = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly StubFixture _fixture = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Mock<IVersionLinker> _versions = new();
    private readonly CapturingLogger<StubCleanupTask> _logger = new();
    private readonly CapturingLogger<LocalTitleLinker> _linkerLogger = new();
    private readonly Dictionary<string, BaseItem> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid User, string Path), UserItemData> _rows = [];
    private readonly User _anna = new("anna", "auth", "reset");
    private readonly User _bruno = new("bruno", "auth", "reset");
    private List<VirtualFolderInfo> _folders = [];
    private RemoteServerInfo _server = null!;

    public SplitTitleTests()
    {
        _fixture.Library.Setup(library => library.GetVirtualFolders()).Returns(() => [.. _folders]);
        _fixture.Library
            .Setup(library => library.FindByPath(It.IsAny<string>(), It.IsAny<bool?>()))
            .Returns((string path, bool? _) => _items.GetValueOrDefault(path));
        _fixture.Library
            .Setup(library => library.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()))
            .Callback<BaseItem, DeleteOptions>((item, _) => _items.Remove(item.Path));
        _userData
            .Setup(data => data.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((User user, BaseItem item) => WatchStateTransfer.Copy(Row(user, item.Path)));
        _userData
            .Setup(data => data.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()))
            .Callback<User, BaseItem, UserItemData, UserDataSaveReason, CancellationToken>((user, item, data, _, _) => _rows[(user.Id, item.Path)] = WatchStateTransfer.Copy(data));
        _versions.Setup(linker => linker.DetachForRemovalAsync(It.IsAny<IReadOnlyCollection<StubEntry>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
    }

    private IReadOnlyList<StubEntry> Stubs => _fixture.Registry.State.Stubs;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ATitleInTwoLocalLibrariesIsTwoStubsThatAreVersionsOfEachOther()
    {
        await AddFilmAsync();
        Apart(TestLibraries.Films, TestLibraries.FilmsB);

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal("a", Assert.Single(StubIn(TestLibraries.Films).Sources).ItemId);
        Assert.Equal("b", Assert.Single(StubIn(TestLibraries.FilmsB).Sources).ItemId);
        Assert.Equal(Stubs.Select(stub => stub.Id).Order(StringComparer.Ordinal), Assert.Single(_fixture.Registry.State.Groups).StubIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ATitleInOneLocalLibraryIsOneStubWithTheOtherCopyAsItsSource()
    {
        await AddFilmAsync();
        Together(TestLibraries.Films, TestLibraries.FilmsB);

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(["a", "b"], Assert.Single(Stubs).Sources.Select(source => source.ItemId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheScanDoesNotMergeTheStubsOfTwoLocalLibraries()
    {
        await AddFilmAsync();
        Apart(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();

        await ScanAsync();

        Assert.Equal(2, Stubs.Count);
        Assert.All(Stubs, stub => Assert.True(File.Exists(PathOf(stub))));
    }

    // A folder moved by hand is in no library for a moment, and the removal starts a scan.
    [Fact]
    public async Task ALibraryThatChangesWithNoSyncSinceMergesNothingAtTheScan()
    {
        await AddFilmAsync();
        Apart(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        Together(TestLibraries.Films);

        await ScanAsync();

        Assert.Equal(2, Stubs.Count);
    }

    [Fact]
    public async Task ANewLocalFilmJoinsTheStubsOfBothLocalLibraries()
    {
        await AddFilmAsync();
        Apart(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        var local = Guid.NewGuid();
        _fixture.LocalCatalog.Items.Add(new CatalogItem { ServerId = "local", ItemId = local.ToString("N"), Kind = ContentKind.Movie, Title = "Film", Year = 2020, Ids = new ContentIds("1") });

        await Linker().LinkAsync(local, CancellationToken.None);

        var group = Assert.Single(_fixture.Registry.State.Groups);
        Assert.Equal([local], group.LocalItemIds);
        Assert.Equal(Stubs.Select(stub => stub.Id).Order(StringComparer.Ordinal), group.StubIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task NothingIsPlannedWhileTheLibrariesCannotBeRead()
    {
        await AddFilmAsync();
        Apart(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        var files = _fixture.Files;
        var state = _fixture.Registry.State;
        _fixture.Library.Setup(library => library.GetVirtualFolders()).Throws(new IOException("busy"));

        await Assert.ThrowsAsync<IOException>(() => _fixture.Materializer.MaterializeAsync(CancellationToken.None));
        await ScanAsync();
        await Linker().LinkAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(files, _fixture.Files);
        Assert.Equal(state.Stubs.Select(stub => stub.RelativePath), Stubs.Select(stub => stub.RelativePath));
        Assert.Contains(_logger.Lines, line => line.Contains(nameof(IOException), StringComparison.Ordinal));
        Assert.Contains(_linkerLogger.Lines, line => line.Contains(nameof(IOException), StringComparison.Ordinal));
        _versions.Verify(linker => linker.LinkAsync(false, It.IsAny<CancellationToken>()), Times.Once);
        _versions.Verify(linker => linker.LinkLocalAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Row 507 (c).
    [Fact]
    public async Task AFilmThatSplitsGivesItsNewStubTheFullStateOfEachUserOnce()
    {
        await AddFilmAsync();
        Together(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        var old = Assert.Single(Stubs);
        Give(_anna, PathOf(old), Full());
        Give(_bruno, PathOf(old), new UserItemData { Key = "k", PlaybackPositionTicks = 7, LastPlayedDate = _day });
        Apart(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();

        var added = Assert.Single(Stubs, stub => stub.Id != old.Id);
        AssertFull(Row(_anna, PathOf(added)));
        AssertFull(Row(_anna, PathOf(old)));
        Assert.Equal(7, Row(_bruno, PathOf(added)).PlaybackPositionTicks);

        // Only once: what the user takes off later stays off.
        Give(_anna, PathOf(added), new UserItemData { Key = "k" });
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        Assert.False(Row(_anna, PathOf(added)).IsFavorite);
        Assert.False(Row(_anna, PathOf(added)).Played);
    }

    [Fact]
    public async Task ASplitWaitsForTheItemOfItsNewStubAlsoAcrossARestart()
    {
        await AddFilmAsync();
        Together(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        var old = Assert.Single(Stubs);
        Give(_anna, PathOf(old), Full());
        Apart(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        var added = Assert.Single(Stubs, stub => stub.Id != old.Id);
        await CleanAsync();
        await _fixture.SyncAndMaterializeAsync();

        using var reopened = new StubRegistry(_fixture.Database, _fixture.Sync.Store, new CapturingLogger<StubRegistry>());
        AddItems();
        await CleanAsync(reopened);

        AssertFull(Row(_anna, PathOf(added)));
    }

    [Fact]
    public async Task ASplitLeavesANewerStateOfTheNewStubAlone()
    {
        await AddFilmAsync();
        Together(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        var old = Assert.Single(Stubs);
        Give(_anna, PathOf(old), Full());
        Apart(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        var added = Assert.Single(Stubs, stub => stub.Id != old.Id);
        AddItems();
        Give(_anna, PathOf(added), new UserItemData { Key = "k", PlaybackPositionTicks = 3, LastPlayedDate = _day.AddDays(1) });

        await CleanAsync();

        var row = Row(_anna, PathOf(added));
        Assert.Equal((3, false), (row.PlaybackPositionTicks, row.Played));
        Assert.True(row.IsFavorite);
    }

    // A new copy in another local library is a version, which only aligns "played" (row 63).
    [Fact]
    public async Task ANewCopyInAnotherLocalLibraryIsNoSplit()
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.FilmsB, "Film B", "movies"));
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("a", "Film", 2020, "1"));
        _server = await _fixture.Sync.AddServerAsync("A", TestLibraries.Films, TestLibraries.FilmsB);
        Apart(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        Give(_anna, PathOf(StubIn(TestLibraries.Films)), Full());
        _fixture.Sync.Client.Add(TestLibraries.FilmsB, RemoteItemType.Movie, TestItems.Movie("b", "Film", 2020, "1"));

        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();

        Assert.False(Row(_anna, PathOf(StubIn(TestLibraries.FilmsB))).IsFavorite);
    }

    [Fact]
    public async Task TheEpisodesOfAnotherLocalLibraryMoveToASeriesOfTheirOwnWithTheirState()
    {
        await AddSeriesAsync(("e1", TestLibraries.Shows, 1, 1), ("e2", TestLibraries.ShowsB, 2, 1));
        Together(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        var series = Assert.Single(_fixture.Registry.State.Series);
        var old = Stubs.Single(stub => stub.SeasonNumber == 2);
        Give(_anna, PathOf(old), Full());
        Give(_anna, _fixture.Path_(series.RelativePath), new UserItemData { Key = "k", IsFavorite = true, Rating = 8 });
        Apart(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);

        await _fixture.SyncAndMaterializeAsync();
        Assert.True(File.Exists(PathOf(old)));
        await ScanAsync();
        var added = Assert.Single(Stubs, stub => stub.SeasonNumber == 2 && stub.Id != old.Id);
        var addedSeries = Assert.Single(_fixture.Registry.State.Series, entry => entry.Id != series.Id);
        Assert.StartsWith(StubFixture.InLibrary(_server, TestLibraries.ShowsB), added.RelativePath, StringComparison.Ordinal);
        Assert.Equal(addedSeries.Id, added.SeriesStubId);
        AssertFull(Row(_anna, PathOf(added)));
        var row = Row(_anna, _fixture.Path_(addedSeries.RelativePath));
        Assert.Equal((true, 8d), (row.IsFavorite, row.Rating));

        await _fixture.SyncAndMaterializeAsync();
        Assert.DoesNotContain(Stubs, stub => stub.Id == old.Id);
        Assert.Contains(_fixture.Registry.State.Series, entry => entry.Id == series.Id);
        Assert.True(File.Exists(PathOf(Stubs.Single(stub => stub.SeasonNumber == 1))));
    }

    // Jellyfin caches an instance of each item with its rows: a save on another would leave them old, and a later save would undo it.
    [Fact]
    public async Task AHandoverSavesOnTheInstanceThatJellyfinCaches()
    {
        await AddFilmAsync();
        Together(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        var old = Assert.Single(Stubs);
        Give(_anna, PathOf(old), Full());
        Apart(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        AddItems();
        var added = _items[PathOf(Assert.Single(Stubs, stub => stub.Id != old.Id))];
        var cached = new Movie { Id = added.Id, Path = added.Path };
        _fixture.Library.Setup(library => library.GetItemById(added.Id)).Returns(cached);

        await CleanAsync();

        AssertFull(Row(_anna, added.Path));
        _userData.Verify(data => data.SaveUserData(_anna, It.Is<BaseItem>(item => ReferenceEquals(item, cached)), It.IsAny<UserItemData>(), UserDataSaveReason.Import, It.IsAny<CancellationToken>()), Times.Once);
        _userData.Verify(data => data.SaveUserData(_anna, It.Is<BaseItem>(item => ReferenceEquals(item, added)), It.IsAny<UserItemData>(), UserDataSaveReason.Import, It.IsAny<CancellationToken>()), Times.Never);
    }

    // A file of two episodes is a backup of both, so it alone does not make two episodes one title.
    [Fact]
    public async Task AFileOfTwoEpisodesDoesNotMixTheirStates()
    {
        await AddSeriesAsync(("a1", TestLibraries.Shows, 1, 1), ("a2", TestLibraries.Shows, 1, 2), ("b1", TestLibraries.ShowsB, 1, 1), ("b2", TestLibraries.ShowsB, 1, 2));
        _fixture.Sync.Client.Add(TestLibraries.ShowsB, RemoteItemType.Episode, TestItems.Episode("x", "s" + TestLibraries.ShowsB, 1, 1) with { IndexNumberEnd = 2, MediaSources = [TestItems.Source("x", 480, 500_000)] });
        Together(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        var first = Stubs.Single(stub => (stub.EpisodeNumber, stub.EpisodeNumberEnd ?? 1) == (1, 1));
        var second = Stubs.Single(stub => stub.EpisodeNumber == 2);
        Assert.All(new[] { first, second }, stub => Assert.Contains(stub.Sources, source => source.ItemId == "x"));
        Give(_anna, PathOf(first), Full());
        Give(_anna, PathOf(second), new UserItemData { Key = "k", PlaybackPositionTicks = 7, LastPlayedDate = _day.AddDays(1) });
        Apart(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);

        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();

        var added = Stubs.Where(stub => stub.Id != first.Id && stub.Id != second.Id).ToList();
        AssertFull(Row(_anna, PathOf(added.Single(stub => (stub.EpisodeNumber, stub.EpisodeNumberEnd ?? 1) == (1, 1)))));
        var row = Row(_anna, PathOf(added.Single(stub => stub.EpisodeNumber == 2)));
        Assert.Equal((7, false), (row.PlaybackPositionTicks, row.Played));
    }

    // The file of two episodes backs both, so its stub joins the one of its own episode.
    [Fact]
    public async Task AStubOfAFileOfTwoEpisodesJoinsTheStubOfItsEpisode()
    {
        await AddSeriesAsync(("a1", TestLibraries.Shows, 1, 1), ("a2", TestLibraries.Shows, 1, 2), ("a3", TestLibraries.Shows, 1, 2), ("b1", TestLibraries.ShowsB, 1, 1));
        _fixture.Sync.Client.Add(TestLibraries.ShowsB, RemoteItemType.Episode, TestItems.Episode("x", "s" + TestLibraries.ShowsB, 1, 1) with { IndexNumberEnd = 2, MediaSources = [TestItems.Source("x", 480, 500_000)] });
        Apart(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        var stub = Assert.Single(Stubs, stub => stub.Sources.Count == 1 && stub.Sources[0].ItemId == "x");
        Give(_anna, PathOf(stub), Full());
        Together(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);

        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();

        var first = Assert.Single(Stubs, stub => stub.Sources.Any(source => source.ItemId == "a1"));
        var second = Assert.Single(Stubs, stub => stub.Sources.Any(source => source.ItemId == "a2"));
        AssertFull(Row(_anna, PathOf(second)));
        Assert.False(Row(_anna, PathOf(first)).Played);
        Assert.DoesNotContain(Stubs, left => left.Id == stub.Id);
    }

    // No users may mean they cannot be read for now.
    [Fact]
    public async Task AJoinWaitsWhileThereAreNoUsersToHandTheStateOver()
    {
        await AddFilmAsync();
        Apart(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        Together(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        AddItems();

        await CleanAsync(users: []);

        Assert.Equal(2, Stubs.Count);
    }

    [Fact]
    public async Task AFilmThatJoinsHandsTheStateOverAndKeepsOneStub()
    {
        await AddFilmAsync();
        Apart(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        var left = StubIn(TestLibraries.Films);
        var right = StubIn(TestLibraries.FilmsB);
        Give(_anna, PathOf(left), new UserItemData { Key = "k", PlaybackPositionTicks = 3, LastPlayedDate = _day.AddDays(-1) });
        Give(_anna, PathOf(right), Full());
        Give(_bruno, PathOf(left), new UserItemData { Key = "k", IsFavorite = true });
        Together(TestLibraries.Films, TestLibraries.FilmsB);

        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();

        var kept = Assert.Single(Stubs);
        Assert.Equal(["a", "b"], kept.Sources.Select(source => source.ItemId).Order(StringComparer.Ordinal));
        var row = Row(_anna, PathOf(kept));
        Assert.Equal((true, true, _day), (row.Played, row.IsFavorite, row.LastPlayedDate));
        Assert.True(Row(_bruno, PathOf(kept)).IsFavorite);
        Assert.False(File.Exists(PathOf(kept.Id == left.Id ? right : left)));
        Assert.Empty(_fixture.Registry.State.Groups);
    }

    [Fact]
    public async Task ASeriesThatJoinsKeepsOneSeriesWithTheFavoriteAndTheEpisodesOfBoth()
    {
        await AddSeriesAsync(("e1", TestLibraries.Shows, 1, 1), ("e1b", TestLibraries.ShowsB, 1, 1));
        Apart(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        Assert.Equal(2, _fixture.Registry.State.Series.Count);
        var seriesPaths = _fixture.Registry.State.Series.Select(series => _fixture.Path_(series.RelativePath)).ToList();
        foreach (var path in seriesPaths)
        {
            Give(path == seriesPaths[0] ? _anna : _bruno, path, new UserItemData { Key = "k", IsFavorite = true, Rating = 8 });
        }

        Together(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();

        Assert.Single(Stubs);
        var kept = Assert.Single(_fixture.Registry.State.Series);
        Assert.All(new[] { _anna, _bruno }, user => Assert.True(Row(user, _fixture.Path_(kept.RelativePath)).IsFavorite));
        Assert.Single(seriesPaths, Directory.Exists);
    }

    // Row 537: a stub of two episodes gives its state to both, also when the files of the other library back one episode each.
    [Fact]
    public async Task AStubOfTwoEpisodesThatSplitsGivesItsStateToEachOfThem()
    {
        await AddSeriesAsync(("b1", TestLibraries.ShowsB, 1, 1), ("b2", TestLibraries.ShowsB, 1, 2));
        AddDouble("x", TestLibraries.Shows, 2160);
        Together(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        var old = Assert.Single(Stubs);
        Assert.Equal((1, 2), (old.EpisodeNumber, old.EpisodeNumberEnd));
        Give(_anna, PathOf(old), Full());
        Apart(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);

        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();

        Assert.Equal(3, Stubs.Count);
        Assert.All(Stubs, stub => AssertFull(Row(_anna, PathOf(stub))));
    }

    [Fact]
    public async Task AStubOfTwoEpisodesRemembersItsCopiesOfOneAcrossARestart()
    {
        await AddSeriesAsync(("b1", TestLibraries.ShowsB, 1, 1), ("b2", TestLibraries.ShowsB, 1, 2));
        AddDouble("x", TestLibraries.Shows, 2160);
        Together(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        Assert.Equal(["b1", "b2"], Reopened());

        Apart(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        Assert.Empty(Reopened());

        List<string> Reopened()
        {
            using var reopened = new StubRegistry(_fixture.Database, _fixture.Sync.Store, new CapturingLogger<StubRegistry>());
            return reopened.State.Stubs.SelectMany(stub => stub.PartCopies).Order(StringComparer.Ordinal).ToList();
        }
    }

    // A new copy is a version, so it takes nothing (row 63).
    [Fact]
    public async Task ANewCopyOfOneEpisodeInAnotherLocalLibraryIsNoSplit()
    {
        await AddSeriesAsync(("b9", TestLibraries.ShowsB, 1, 9));
        AddDouble("x", TestLibraries.Shows, 2160);
        Apart(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        Give(_anna, PathOf(StubOf(1, 2)), Full());
        _fixture.Sync.Client.Add(TestLibraries.ShowsB, RemoteItemType.Episode, TestItems.Episode("b1", "s" + TestLibraries.ShowsB, 1, 1));

        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();

        Assert.False(Row(_anna, PathOf(StubOf(1, null))).Played);
    }

    // Row 537: never from one episode to a stub of several.
    [Fact]
    public async Task AStubOfOneEpisodeThatSplitsGivesNothingToAStubOfTwo()
    {
        await AddSeriesAsync(("b1", TestLibraries.ShowsB, 1, 1), ("b2", TestLibraries.ShowsB, 1, 2));
        AddDouble("x", TestLibraries.Shows, 480);
        Together(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        Give(_anna, PathOf(StubOf(1, null)), Full());
        Apart(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);

        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();

        Assert.False(Row(_anna, PathOf(StubOf(1, 2))).Played);
    }

    [Fact]
    public async Task AStubOfTwoEpisodesThatKeepsOneGivesItsStateToTheOther()
    {
        await AddSeriesAsync(("b9", TestLibraries.Shows, 1, 9));
        AddDouble("x", TestLibraries.Shows, 480);
        Together(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        Give(_anna, PathOf(StubOf(1, 2)), Full());
        _fixture.Sync.Client.Add(TestLibraries.Shows, RemoteItemType.Episode, TestItems.Episode("a1", "s" + TestLibraries.Shows, 1, 1));

        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();

        AssertFull(Row(_anna, PathOf(StubOf(1, null))));
        AssertFull(Row(_anna, PathOf(StubOf(2, null))));
        Assert.False(Row(_anna, PathOf(StubOf(9, null))).Played);
    }

    // Row 537: reunited, the state goes to all the episodes covered.
    [Fact]
    public async Task AStubOfTwoEpisodesThatJoinsGivesItsStateToEachOfThem()
    {
        await JoinDoubleAsync(480, "b1", "b2");

        Assert.Equal(2, Stubs.Count);
        AssertFull(Row(_anna, PathOf(StubOf(1, null))));
        AssertFull(Row(_anna, PathOf(StubOf(2, null))));
    }

    [Fact]
    public async Task AStubOfTwoEpisodesThatJoinsTheStubOfOneGivesItsStateToBoth()
    {
        await JoinDoubleAsync(480, "b1");

        Assert.Equal(2, Stubs.Count);
        AssertFull(Row(_anna, PathOf(StubOf(1, null))));
        AssertFull(Row(_anna, PathOf(StubOf(2, null))));
    }

    [Fact]
    public async Task TheStubsOfTwoEpisodesThatJoinAStubOfBothLeave()
    {
        var old = await JoinDoubleAsync(2160, "b1", "b2");

        var kept = Assert.Single(Stubs);
        Assert.Equal(old.Id, kept.Id);
        Assert.Equal((1, 2), (kept.EpisodeNumber, kept.EpisodeNumberEnd));
        AssertFull(Row(_anna, PathOf(kept)));
    }

    [Fact]
    public async Task AStubOfTwoEpisodesWaitsForTheItemOfEach()
    {
        var old = await JoinDoubleAsync(480, ["b1", "b2"], scan: false);
        AddItems();
        var second = PathOf(StubOf(2, null));
        Assert.True(_items.Remove(second, out var item));

        await CleanAsync();
        Assert.Contains(Stubs, stub => stub.Id == old.Id);

        _items[second] = item!;
        await CleanAsync();
        Assert.DoesNotContain(Stubs, stub => stub.Id == old.Id);
        AssertFull(Row(_anna, second));
    }

    // Row 537: the wait closes when the old stub or the new one goes.
    [Fact]
    public async Task ASplitClosesWhenTheCopiesOfItsOldStubAreGone()
    {
        var (old, added) = await WaitingSplitAsync();
        Remove(TestLibraries.Films, RemoteItemType.Movie, "a");

        await _fixture.SyncAndMaterializeAsync();

        Assert.Empty(_fixture.Registry.State.Splits);
        Assert.Equal(added.Id, Assert.Single(Split).Id);
        Assert.False(File.Exists(PathOf(old)));
    }

    [Fact]
    public async Task ASplitClosesWhenItsNewStubIsGone()
    {
        var (old, _) = await WaitingSplitAsync();
        Remove(TestLibraries.FilmsB, RemoteItemType.Movie, "b");

        await _fixture.SyncAndMaterializeAsync();

        Assert.Empty(_fixture.Registry.State.Splits);
        Assert.Equal(old.Id, Assert.Single(Split).Id);
    }

    [Fact]
    public async Task ASplitClosesWhenTheFileOfItsOldStubIsGone()
    {
        var (old, _) = await WaitingSplitAsync();
        File.Delete(PathOf(old));
        _items.Remove(PathOf(old));

        await CleanAsync();

        Assert.Empty(_fixture.Registry.State.Splits);
    }

    // A disk not mounted holds no file either.
    [Fact]
    public async Task ASplitWaitsWhileTheFolderOfItsOldStubCannotBeSeen()
    {
        var (old, _) = await WaitingSplitAsync();
        _items.Remove(PathOf(old));
        Directory.Move(_fixture.Root, _fixture.Root + "-away");
        try
        {
            await CleanAsync();
        }
        finally
        {
            Directory.Move(_fixture.Root + "-away", _fixture.Root);
        }

        Assert.Single(_fixture.Registry.State.Splits);
    }

    // The old item may come back with its folder, and its state with it.
    [Fact]
    public async Task ASplitWaitsWhileTheItemOfItsOldStubIsMissing()
    {
        var (old, _) = await WaitingSplitAsync();
        AddItems();
        _items.Remove(PathOf(old));

        await CleanAsync();

        Assert.Single(_fixture.Registry.State.Splits);
    }

    private static UserItemData Full() => new()
    {
        Key = "k",
        Played = true,
        PlayCount = 2,
        PlaybackPositionTicks = 42,
        LastPlayedDate = _day,
        IsFavorite = true,
        Rating = 9
    };

    private static void AssertFull(UserItemData row)
    {
        Assert.Equal((true, 2, 42L, (DateTime?)_day), (row.Played, row.PlayCount, row.PlaybackPositionTicks, row.LastPlayedDate));
        Assert.Equal((true, (double?)9), (row.IsFavorite, row.Rating));
    }

    private UserItemData Row(User user, string path) => _rows.GetValueOrDefault((user.Id, path)) ?? new UserItemData { Key = "k" };

    private void Give(User user, string path, UserItemData data) => _rows[(user.Id, path)] = data;

    private string PathOf(StubEntry stub) => _fixture.Path_(stub.RelativePath);

    private StubEntry StubIn(string libraryId) =>
        Assert.Single(Stubs, stub => FederatedPaths.RemoteLibraryOf(stub.RelativePath) == libraryId);

    private IEnumerable<StubEntry> Split => Stubs.Where(stub => stub.Sources.Any(source => source.ItemId is "a" or "b"));

    private StubEntry StubOf(int first, int? last) => Assert.Single(Stubs, stub => (stub.EpisodeNumber, stub.EpisodeNumberEnd) == (first, last));

    // A file of the first two episodes, the worst or the best copy by its height.
    private void AddDouble(string id, string library, int height)
    {
        if (!_fixture.Sync.Client.Items.ContainsKey((library, RemoteItemType.Series)))
        {
            _fixture.Sync.Client.Add(library, RemoteItemType.Series, TestItems.Series("s" + library));
        }

        _fixture.Sync.Client.Add(library, RemoteItemType.Episode, TestItems.Episode(id, "s" + library, 1, 1) with { IndexNumberEnd = 2, MediaSources = [TestItems.Source(id, height, 500_000)] });
    }

    private void Remove(string library, RemoteItemType type, string id) => _fixture.Sync.Client.Items[(library, type)].RemoveAll(item => item.Id == id);

    private Task<StubEntry> JoinDoubleAsync(int height, params string[] singles) => JoinDoubleAsync(height, singles, true);

    // A file of two episodes in one local library and files of one in another, then both in one. Gives the stub of the two.
    private async Task<StubEntry> JoinDoubleAsync(int height, string[] singles, bool scan)
    {
        await AddSeriesAsync(singles.Select((id, index) => (id, TestLibraries.ShowsB, 1, index + 1)).ToArray());
        AddDouble("x", TestLibraries.Shows, height);
        Apart(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        var old = StubOf(1, 2);
        Give(_anna, PathOf(old), Full());
        Together(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        if (scan)
        {
            await ScanAsync();
        }

        return old;
    }

    // A film that split, before the scan that makes the item of its new stub.
    private async Task<(StubEntry Old, StubEntry Added)> WaitingSplitAsync()
    {
        await AddFilmAsync();
        Together(TestLibraries.Films, TestLibraries.FilmsB);

        // A library that a removal leaves empty keeps its stubs, so each holds another film.
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("y", "Altro", 2021, "2"));
        _fixture.Sync.Client.Add(TestLibraries.FilmsB, RemoteItemType.Movie, TestItems.Movie("z", "Terzo", 2022, "3"));
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        var old = Assert.Single(Split);
        Apart(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        Assert.Single(_fixture.Registry.State.Splits);
        return (old, Assert.Single(Split, stub => stub.Id != old.Id));
    }

    private VirtualFolderInfo Local(string name, TypeFolder type, params string[] libraries) => new()
    {
        Name = name,
        ItemId = name,
        Locations = libraries.Select(library => FederatedFolderLayout.TypeFolderPath(_fixture.Root, _server.Id, library, type)).ToArray()
    };

    private void Together(params string[] libraries) => Together(TypeFolder.Movies, libraries);

    private void Together(TypeFolder type, params string[] libraries) => _folders = [Local("Tutti", type, libraries)];

    private void Apart(params string[] libraries) => Apart(TypeFolder.Movies, libraries);

    private void Apart(TypeFolder type, params string[] libraries) => _folders = libraries.Select(library => Local("Di " + library, type, library)).ToList();

    private async Task AddFilmAsync()
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.FilmsB, "Film B", "movies"));
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("a", "Film", 2020, "1"));
        _fixture.Sync.Client.Add(TestLibraries.FilmsB, RemoteItemType.Movie, TestItems.Movie("b", "Film", 2020, "1"));
        _server = await _fixture.Sync.AddServerAsync("A", TestLibraries.Films, TestLibraries.FilmsB);
    }

    private async Task AddSeriesAsync(params (string Id, string Library, int Season, int Number)[] episodes)
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.ShowsB, "Serie B", "tvshows"));
        foreach (var library in episodes.Select(episode => episode.Library).Distinct(StringComparer.Ordinal))
        {
            _fixture.Sync.Client.Add(library, RemoteItemType.Series, TestItems.Series("s" + library));
        }

        foreach (var (id, library, season, number) in episodes)
        {
            _fixture.Sync.Client.Add(library, RemoteItemType.Episode, TestItems.Episode(id, "s" + library, season, number));
        }

        _server = await _fixture.Sync.AddServerAsync("A", TestLibraries.Shows, TestLibraries.ShowsB);
    }

    private LocalTitleLinker Linker() => new(
        _fixture.Registry,
        _fixture.Resolver,
        _fixture.Sync.Index,
        _fixture.Sync.Registry,
        _fixture.Sync.Store,
        _fixture.Library.Object,
        _versions.Object,
        _linkerLogger);

    // A scan makes the items of the stubs, and the post-scan task runs after it.
    private Task ScanAsync()
    {
        AddItems();
        return CleanAsync();
    }

    private void AddItems()
    {
        foreach (var stub in Stubs)
        {
            _items.TryAdd(PathOf(stub), stub.Kind == StubKind.Movie ? new Movie { Id = Guid.NewGuid(), Path = PathOf(stub) } : new Episode { Id = Guid.NewGuid(), Path = PathOf(stub) });
        }

        foreach (var path in _fixture.Registry.State.Series.Select(series => _fixture.Path_(series.RelativePath)))
        {
            _items.TryAdd(path, new Series { Id = Guid.NewGuid(), Path = path });
        }
    }

    private Task CleanAsync(StubRegistry? registry = null, IReadOnlyList<User>? users = null)
    {
        var manager = new Mock<IUserManager>();
        manager.Setup(mock => mock.GetUsers()).Returns(users ?? [_anna, _bruno]);
        var task = new StubCleanupTask(
            registry ?? _fixture.Registry,
            _fixture.Resolver,
            _fixture.Sync.Index,
            _fixture.Sync.Registry,
            _fixture.Sync.Store,
            _fixture.Library.Object,
            manager.Object,
            _userData.Object,
            Mock.Of<IProviderManager>(),
            Mock.Of<IFileSystem>(),
            _fixture.Remover,
            _logger,
            _versions.Object);
        return task.Run(new Progress<double>(), CancellationToken.None);
    }
}
