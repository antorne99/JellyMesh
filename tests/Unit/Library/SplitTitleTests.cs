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
    private const string MusicB = "a0d1000000000000000000000000000b";
    private const string MusicVideosB = "c11b000000000000000000000000000b";
    private const string HomeVideosB = "40e0000000000000000000000000000b";
    private const string BooksB = "b00c000000000000000000000000000b";

    private static readonly DateTime _day = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly StubFixture _fixture = new();
    private readonly Mock<IVersionLinker> _versions = new();
    private readonly CapturingLogger<StubCleanupTask> _logger = new();
    private readonly CapturingLogger<LocalTitleLinker> _linkerLogger = new();
    private readonly Dictionary<string, BaseItem> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid User, string Path), UserItemData> _rows = [];
    private readonly User _anna = new("anna", "auth", "reset");
    private readonly User _bruno = new("bruno", "auth", "reset");
    private List<VirtualFolderInfo> _folders = [];
    private List<User> _users;
    private RemoteServerInfo _server = null!;

    public SplitTitleTests()
    {
        _users = [_anna, _bruno];
        _fixture.Library.Setup(library => library.GetVirtualFolders()).Returns(() => [.. _folders]);
        _fixture.Library
            .Setup(library => library.FindByPath(It.IsAny<string>(), It.IsAny<bool?>()))
            .Returns((string path, bool? _) => _items.GetValueOrDefault(path));
        _fixture.Library
            .Setup(library => library.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()))
            .Callback<BaseItem, DeleteOptions>((item, _) => _items.Remove(item.Path));
        _fixture.UserData
            .Setup(data => data.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((User user, BaseItem item) => WatchStateTransfer.Copy(Row(user, item.Path)));
        _fixture.UserData
            .Setup(data => data.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()))
            .Callback<User, BaseItem, UserItemData, UserDataSaveReason, CancellationToken>((user, item, data, _, _) => _rows[(user.Id, item.Path)] = WatchStateTransfer.Copy(data));
        _fixture.Users.Setup(manager => manager.GetUsers()).Returns(() => _users);
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

    // Row 507 holds for every type, with no special cases (§10 row 562).
    [Theory]
    [InlineData(StubKind.Track)]
    [InlineData(StubKind.MusicVideo)]
    [InlineData(StubKind.HomeVideo)]
    [InlineData(StubKind.Photo)]
    [InlineData(StubKind.Book)]
    [InlineData(StubKind.AudioBook)]
    public async Task ATitleOfAnyTypeThatSplitsGivesItsNewStubTheFullState(StubKind kind)
    {
        var type = TypeFolder.Of(kind);
        var (first, second) = await AddTitleAsync(kind);
        Together(type, first, second);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        var old = Assert.Single(Stubs, stub => stub.Kind == kind);
        Give(_anna, PathOf(old), Full());
        Apart(type, first, second);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();

        var added = Assert.Single(Stubs, stub => stub.Kind == kind && stub.Id != old.Id);
        Assert.NotEqual(FederatedPaths.RemoteLibraryOf(old.RelativePath), FederatedPaths.RemoteLibraryOf(added.RelativePath));
        AssertFull(Row(_anna, PathOf(added)));
        AssertFull(Row(_anna, PathOf(old)));
        Assert.Empty(_fixture.Registry.State.Splits);
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
        _fixture.UserData.Verify(data => data.SaveUserData(_anna, It.Is<BaseItem>(item => ReferenceEquals(item, cached)), It.IsAny<UserItemData>(), UserDataSaveReason.Import, It.IsAny<CancellationToken>()), Times.Once);
        _fixture.UserData.Verify(data => data.SaveUserData(_anna, It.Is<BaseItem>(item => ReferenceEquals(item, added)), It.IsAny<UserItemData>(), UserDataSaveReason.Import, It.IsAny<CancellationToken>()), Times.Never);
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

    // Row 565: a music video that joins is merged into the stub left, like a film, and hands its state over.
    [Fact]
    public async Task AMusicVideoThatJoinsHandsTheStateOverAndKeepsOneStub()
    {
        var type = TypeFolder.Of(StubKind.MusicVideo);
        var (first, second) = await AddTitleAsync(StubKind.MusicVideo);
        Apart(type, first, second);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        var (left, right) = (StubIn(first), StubIn(second));
        Give(_anna, PathOf(left), Full());
        Give(_bruno, PathOf(right), new UserItemData { Key = "k", IsFavorite = true });
        Together(type, first, second);

        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();

        var kept = Assert.Single(Stubs, stub => stub.Kind == StubKind.MusicVideo);
        AssertFull(Row(_anna, PathOf(kept)));
        Assert.True(Row(_bruno, PathOf(kept)).IsFavorite);
        Assert.False(File.Exists(PathOf(kept.Id == left.Id ? right : left)));
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

    // Row 543: the state of one episode is not the state of a stub of several.
    [Fact]
    public async Task TheStateOfOneEpisodeIsNotGivenToAStubOfTwo()
    {
        var old = await JoinDoubleAsync(2160, ["b1", "b2"], scan: false);
        var singles = Stubs.Where(stub => stub.Id != old.Id).ToList();
        var leaving = SeriesPathOf(singles[0]);
        Assert.Equal(2, singles.Count);
        Assert.All(singles, stub => Give(_bruno, PathOf(stub), Full()));
        Give(_bruno, leaving, Full());

        await ScanAsync();

        var kept = Assert.Single(Stubs);
        AssertEmpty(Row(_bruno, PathOf(kept)));
        AssertFull(Row(_anna, PathOf(kept)));
        Assert.NotEqual(leaving, SeriesPathOf(kept));
        Assert.Equal((true, (double?)9), (Row(_bruno, SeriesPathOf(kept)).IsFavorite, Row(_bruno, SeriesPathOf(kept)).Rating));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AStubOfOneEpisodeThatTakesTwoLosesItsState(int number)
    {
        var stub = await AddSingleAsync(number);
        Give(_anna, PathOf(stub), WithStreams(Full()));
        Give(_bruno, PathOf(stub), Full());
        Widen();

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal((stub.Id, stub.RelativePath), (StubOf(1, 2).Id, StubOf(1, 2).RelativePath));
        AssertEmpty(Row(_anna, PathOf(stub)));
        AssertEmpty(Row(_bruno, PathOf(stub)));
        Assert.Equal((3, 4), (Row(_anna, PathOf(stub)).AudioStreamIndex, Row(_anna, PathOf(stub)).SubtitleStreamIndex));
    }

    [Fact]
    public async Task AStateGivenAfterAStubTookTwoEpisodesStays()
    {
        var stub = await AddSingleAsync(1);
        Widen();
        await _fixture.SyncAndMaterializeAsync();
        Give(_anna, PathOf(stub), Full());

        await ScanAsync();
        Replace(3, 720);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();

        Assert.Equal(stub.Id, StubOf(1, 3).Id);
        AssertFull(Row(_anna, PathOf(stub)));
    }

    [Fact]
    public async Task AStubThatTookTwoEpisodesSavesNoEmptyState()
    {
        var stub = await AddSingleAsync(1);
        Give(_anna, PathOf(stub), WithStreams(new UserItemData { Key = "k" }));
        Widen();

        await _fixture.SyncAndMaterializeAsync();

        _fixture.UserData.Verify(
            data => data.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // Saved on the instance Jellyfin caches, as in a hand over.
    [Fact]
    public async Task AStubThatTookTwoEpisodesIsClearedOnTheCachedItem()
    {
        var stub = await AddSingleAsync(1);
        Give(_anna, PathOf(stub), Full());
        var cached = new Episode { Id = _items[PathOf(stub)].Id, Path = PathOf(stub) };
        _fixture.Library.Setup(library => library.GetItemById(cached.Id)).Returns(cached);
        Widen();

        await _fixture.SyncAndMaterializeAsync();

        _fixture.UserData.Verify(
            data => data.SaveUserData(_anna, It.Is<BaseItem>(item => ReferenceEquals(item, cached)), It.IsAny<UserItemData>(), UserDataSaveReason.Import, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // The users cannot be read for now, also across a restart: the state goes at the next scan, or at the next sync.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStubThatTookTwoEpisodesIsClearedWhenItCanBe(bool atScan)
    {
        var stub = await AddSingleAsync(1);
        Give(_anna, PathOf(stub), Full());
        Widen();
        _users = [];
        await _fixture.SyncAndMaterializeAsync();
        await CleanAsync();
        AssertFull(Row(_anna, PathOf(stub)));
        _users = [_anna, _bruno];

        if (atScan)
        {
            using var reopened = new StubRegistry(_fixture.Database, _fixture.Sync.Store, new CapturingLogger<StubRegistry>());
            await CleanAsync(reopened);
        }
        else
        {
            await _fixture.SyncAndMaterializeAsync();
        }

        AssertEmpty(Row(_anna, PathOf(stub)));
    }

    [Fact]
    public async Task AStubThatTookTwoEpisodesIsClearedAgainAfterAFailure()
    {
        var stub = await AddSingleAsync(1);
        Give(_anna, PathOf(stub), Full());
        Widen();
        _fixture.UserData
            .Setup(data => data.GetUserData(_anna, It.IsAny<BaseItem>()))
            .Throws(new TimeoutException("secret"));
        await _fixture.SyncAndMaterializeAsync();
        Assert.Equal(stub.Id, StubOf(1, 2).Id);
        Assert.DoesNotContain(_fixture.Logger.Lines, line => line.Contains("secret", StringComparison.Ordinal));
        _fixture.UserData
            .Setup(data => data.GetUserData(_anna, It.IsAny<BaseItem>()))
            .Returns((User user, BaseItem item) => WatchStateTransfer.Copy(Row(user, item.Path)));

        await CleanAsync();

        AssertEmpty(Row(_anna, PathOf(stub)));
    }

    // Jellyfin may give the row it keeps: a row not saved must not look cleared.
    [Fact]
    public async Task AStubThatTookTwoEpisodesIsClearedAgainAfterAFailedSave()
    {
        var stub = await AddSingleAsync(1);
        var row = Full();
        _fixture.UserData.Setup(data => data.GetUserData(_anna, It.IsAny<BaseItem>())).Returns(row);
        _fixture.UserData
            .Setup(data => data.SaveUserData(_anna, It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()))
            .Throws(new TimeoutException());
        Widen();

        await _fixture.SyncAndMaterializeAsync();

        AssertFull(row);
        Assert.NotNull(StubOf(1, 2).StateOfOneUntil);
    }

    [Fact]
    public async Task AStatePlayedSinceAStubTookTwoEpisodesStays()
    {
        var stub = await AddSingleAsync(1);
        Give(_anna, PathOf(stub), Full());
        Give(_bruno, PathOf(stub), Full());
        Widen();
        _users = [];
        await _fixture.SyncAndMaterializeAsync();
        var since = _fixture.Sync.Clock.Now.UtcDateTime.AddHours(1);
        Give(_anna, PathOf(stub), new UserItemData { Key = "k", PlaybackPositionTicks = 5, LastPlayedDate = since });
        _fixture.Sync.Clock.Now = _fixture.Sync.Clock.Now.AddDays(1);
        await _fixture.SyncAndMaterializeAsync();
        _users = [_anna, _bruno];

        await CleanAsync();

        Assert.Equal((5L, (DateTime?)since), (Row(_anna, PathOf(stub)).PlaybackPositionTicks, Row(_anna, PathOf(stub)).LastPlayedDate));
        AssertEmpty(Row(_bruno, PathOf(stub)));
    }

    // Its folder is in no library for a while: the old state may come back with the item.
    [Fact]
    public async Task AStubThatTookTwoEpisodesIsClearedWhenItsItemIsBack()
    {
        var stub = await AddSingleAsync(1);
        _items.Remove(PathOf(stub), out var item);
        Widen();
        await _fixture.SyncAndMaterializeAsync();
        await CleanAsync();
        _items[PathOf(stub)] = item!;
        Give(_anna, PathOf(stub), Full());

        await CleanAsync();
        AssertEmpty(Row(_anna, PathOf(stub)));

        Give(_anna, PathOf(stub), Full());
        await CleanAsync();
        AssertFull(Row(_anna, PathOf(stub)));
    }

    [Fact]
    public async Task AStubWithTheStateOfOneEpisodeTakesOneWithNoItem()
    {
        var old = await JoinDoubleAsync(2160, ["b1", "b2"], scan: false);
        AddItems();
        var singles = Stubs.Where(stub => stub.Id != old.Id).ToList();
        await MarkAsync(old, singles[0]);
        Assert.All(singles, stub => _items.Remove(PathOf(stub)));

        await CleanAsync();
        Assert.DoesNotContain(Stubs, stub => stub.Id == singles[0].Id);

        await CleanAsync();
        Assert.Equal(old.Id, Assert.Single(Stubs).Id);
    }

    // The item at the old path of a server rename still holds the state that the move gives to the new one.
    [Fact]
    public async Task AStubThatTookTwoEpisodesIsClearedAtItsOldPathToo()
    {
        var stub = await AddSingleAsync(1);
        var old = _fixture.Path_("old.strm");
        _items[old] = new Episode { Id = Guid.NewGuid(), Path = old };
        Give(_anna, old, Full());
        await SaveRenamesAsync(new StubRename(stub.Id, "old.strm"));
        _items.Remove(PathOf(stub), out var item);
        Widen();

        await _fixture.SyncAndMaterializeAsync();
        AssertEmpty(Row(_anna, old));

        _items[PathOf(stub)] = item!;
        Give(_anna, PathOf(stub), Full());
        await CleanAsync();
        AssertEmpty(Row(_anna, PathOf(stub)));
    }

    // Row 507: the stub that splits from it takes the state of its episode first.
    [Fact]
    public async Task AStubThatTakesTwoEpisodesGivesItsStateToTheStubThatSplitsFromIt()
    {
        var first = await SplitAsItWidensAsync();
        Assert.Equal(first.Id, StubOf(1, 2).Id);
        AssertFull(Row(_anna, PathOf(first)));

        await ScanAsync();

        AssertFull(Row(_anna, PathOf(StubOf(1, null))));
        AssertEmpty(Row(_anna, PathOf(first)));
        AssertEmpty(Row(_anna, PathOf(StubOf(2, null))));
    }

    // The wait of the split closes with no hand over.
    [Fact]
    public async Task AStubThatTookTwoEpisodesIsClearedWhenItsSplitIsGone()
    {
        var first = await SplitAsItWidensAsync();
        Remove(TestLibraries.ShowsB, RemoteItemType.Episode, "b1");

        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();

        Assert.DoesNotContain(Stubs, stub => (stub.EpisodeNumber, stub.EpisodeNumberEnd) == (1, null));
        AssertEmpty(Row(_anna, PathOf(first)));
    }

    // Joined again before the scan: its state is still that of the first episode.
    [Fact]
    public async Task AStubThatTookTwoEpisodesAndJoinsAgainGivesItsStateToItsEpisodeAlone()
    {
        var first = await SplitAsItWidensAsync();
        Together(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();

        await ScanAsync();
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();

        Assert.Equal(2, Stubs.Count);
        AssertFull(Row(_anna, PathOf(StubOf(1, null))));
        AssertEmpty(Row(_anna, PathOf(StubOf(2, null))));
        Assert.DoesNotContain(Stubs, stub => stub.EpisodeNumberEnd is not null);
        Assert.NotNull(first);
    }

    // A move to the new name of the server holds the wait of the split, so the state is not cleared yet.
    [Fact]
    public async Task AStubWithTheStateOfOneEpisodeIsNotMergedBeforeItIsCleared()
    {
        var old = await JoinDoubleAsync(480, ["b1", "b2"], scan: false);
        AddItems();
        await MarkAsync(old, StubOf(1, null));

        await CleanAsync();
        Assert.Equal(3, Stubs.Count);
        AssertEmpty(Row(_anna, PathOf(StubOf(2, null))));

        await SaveRenamesAsync();
        await CleanAsync();
        Assert.Equal(2, Stubs.Count);
        AssertFull(Row(_anna, PathOf(StubOf(1, null))));
        AssertEmpty(Row(_anna, PathOf(StubOf(2, null))));
    }

    [Fact]
    public async Task AStubWithTheStateOfOneEpisodeTakesNoOtherBeforeItIsCleared()
    {
        await AddSeriesAsync();
        AddDouble("x", TestLibraries.Shows, 480);
        AddDouble("y", TestLibraries.ShowsB, 2160);
        Apart(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        Together(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        var kept = Assert.Single(Stubs, stub => stub.Sources.Count == 2);
        var merged = Assert.Single(Stubs, stub => stub.Id != kept.Id);
        Give(_anna, PathOf(merged), Full());
        await MarkAsync(kept, merged);

        await CleanAsync();
        Assert.Equal(2, Stubs.Count);

        await SaveRenamesAsync();
        await CleanAsync();
        Assert.Equal(kept.Id, Assert.Single(Stubs).Id);
        AssertFull(Row(_anna, PathOf(kept)));
    }

    [Fact]
    public async Task NothingSplitsFromAStubWithTheStateOfOneEpisode()
    {
        var stub = await AddSingleAsync(1);
        Give(_anna, PathOf(stub), Full());
        Widen();
        AddDouble("y", TestLibraries.ShowsB, 480);
        _users = [];
        await _fixture.SyncAndMaterializeAsync();
        Apart(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        Assert.Equal(2, Stubs.Count);
        _users = [_anna, _bruno];

        await ScanAsync();

        Assert.All(Stubs, entry => AssertEmpty(Row(_anna, PathOf(entry))));
    }

    [Fact]
    public async Task AStubWithTheStateOfOneEpisodeThatIsOfItAgainKeepsIt()
    {
        var stub = await AddSingleAsync(1);
        Give(_anna, PathOf(stub), Full());
        Widen();
        _users = [];
        await _fixture.SyncAndMaterializeAsync();
        _fixture.Sync.Client.Add(TestLibraries.ShowsB, RemoteItemType.Series, TestItems.Series("s" + TestLibraries.ShowsB));
        _fixture.Sync.Client.Add(TestLibraries.ShowsB, RemoteItemType.Episode, TestItems.Episode("b1", "s" + TestLibraries.ShowsB, 1, 1));
        _fixture.Sync.Client.Add(TestLibraries.ShowsB, RemoteItemType.Episode, TestItems.Episode("b2", "s" + TestLibraries.ShowsB, 1, 2));
        await _fixture.SyncAndMaterializeAsync();
        _users = [_anna, _bruno];

        await ScanAsync();

        Assert.Equal(stub.Id, StubOf(1, null).Id);
        AssertFull(Row(_anna, PathOf(stub)));
        AssertEmpty(Row(_anna, PathOf(StubOf(2, null))));
    }

    // The state it waits for, of one episode, comes after it took the second.
    [Fact]
    public async Task AStubThatTookTwoEpisodesBeforeItsScanKeepsNoStateOfOne()
    {
        await SplitAsItWidensAsync();
        Remove(TestLibraries.ShowsB, RemoteItemType.Episode, "b1");
        Remove(TestLibraries.ShowsB, RemoteItemType.Episode, "b2");
        AddDouble("b1", TestLibraries.ShowsB, 480);
        await _fixture.SyncAndMaterializeAsync();

        await ScanAsync();

        Assert.Empty(_fixture.Registry.State.Splits);
        Assert.All(Stubs, stub => AssertEmpty(Row(_anna, PathOf(stub))));
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

    // A merge before the state is handed over would mix the states of the two episodes.
    [Fact]
    public async Task AStubThatASplitWaitsForIsNotMergedYet()
    {
        await AddSeriesAsync();
        AddDouble("x", TestLibraries.Shows, 480);
        Together(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        _fixture.Sync.Client.Add(TestLibraries.ShowsB, RemoteItemType.Series, TestItems.Series("s" + TestLibraries.ShowsB));
        _fixture.Sync.Client.Add(TestLibraries.ShowsB, RemoteItemType.Episode, TestItems.Episode("b1", "s" + TestLibraries.ShowsB, 1, 1));
        _fixture.Sync.Client.Add(TestLibraries.ShowsB, RemoteItemType.Episode, TestItems.Episode("b2", "s" + TestLibraries.ShowsB, 1, 2));
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        Assert.Equal(TestLibraries.Shows, FederatedPaths.RemoteLibraryOf(StubOf(2, null).RelativePath));
        var old = Stubs.ToList();
        Assert.Equal(2, old.Count);
        Give(_anna, PathOf(StubOf(2, null)), Full());
        Apart(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();

        await CleanAsync();
        Assert.All(old, stub => Assert.Contains(Stubs, other => other.Id == stub.Id));

        await ScanAsync();
        Assert.Empty(_fixture.Registry.State.Splits);
        Assert.Equal(3, Stubs.Count);
        AssertFull(Row(_anna, PathOf(StubOf(2, null))));
        Assert.False(Row(_anna, PathOf(StubOf(1, null))).Played);
    }

    // The merge hands the state over, so it does not wait for a stub that may never have an item.
    [Fact]
    public async Task AStubThatSplitAndJoinedAgainIsMergedWithoutItsItem()
    {
        await WaitingSplitAsync();
        Together(TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        var kept = Assert.Single(Split, stub => stub.Sources.Count == 2);
        AddItems();
        _items.Remove(PathOf(Assert.Single(Split, stub => stub.Id != kept.Id)));

        await CleanAsync();

        Assert.Equal(kept.Id, Assert.Single(Split).Id);
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

    // The state of a stub of a renamed server is still at its old path.
    [Fact]
    public async Task ASplitWaitsWhileItsOldStubMovesToTheNameOfItsServer()
    {
        var (old, added) = await WaitingSplitAsync();
        Give(_anna, PathOf(old), Full());
        await SaveRenamesAsync(new StubRename(old.Id, "old.strm"));

        await ScanAsync();
        Assert.Single(_fixture.Registry.State.Splits);

        await SaveRenamesAsync(new StubRename(old.Id, "old.strm") { RemovedItemId = Guid.NewGuid() });
        await ScanAsync();
        Assert.Empty(_fixture.Registry.State.Splits);
        AssertFull(Row(_anna, PathOf(added)));
    }

    [Fact]
    public async Task AnEpisodeGoesWhileASplitWaitsForItsSeries()
    {
        await AddSeriesAsync(("e1", TestLibraries.Shows, 1, 1), ("e2", TestLibraries.Shows, 1, 2), ("f1", TestLibraries.ShowsB, 2, 1), ("f2", TestLibraries.ShowsB, 2, 2));
        Together(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        var first = Assert.Single(Stubs, stub => stub.Sources[0].ItemId == "e1");
        var library = FederatedPaths.RemoteLibraryOf(first.RelativePath)!;
        var gone = library == TestLibraries.Shows ? first : Assert.Single(Stubs, stub => stub.Sources[0].ItemId == "f1");
        Apart(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        Assert.Contains(_fixture.Registry.State.Splits, split => split.FromStubId == first.SeriesStubId);
        Remove(library, RemoteItemType.Episode, gone.Sources[0].ItemId);

        await _fixture.SyncAndMaterializeAsync();

        Assert.DoesNotContain(Stubs, stub => stub.Id == gone.Id);
        Assert.Contains(_fixture.Registry.State.Splits, split => split.FromStubId == first.SeriesStubId);
    }

    // The series is still on its server, so it keeps its state until the new one takes it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASeriesThatLostItsEpisodesWaitsUnlessItIsGoneToo(bool gone)
    {
        await AddSeriesAsync(("e1", TestLibraries.Shows, 1, 1), ("e2", TestLibraries.ShowsB, 2, 1));
        foreach (var library in new[] { TestLibraries.Shows, TestLibraries.ShowsB })
        {
            var other = TestItems.Series("o" + library, "Altra " + library) with { ProviderIds = new Dictionary<string, string?>() };
            _fixture.Sync.Client.Add(library, RemoteItemType.Series, other);
            _fixture.Sync.Client.Add(library, RemoteItemType.Episode, TestItems.Episode("o1" + library, other.Id, 1, 1, other.Name!));
        }

        Together(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        var series = Assert.Single(_fixture.Registry.State.Series, entry => entry.Sources.Count == 2);
        var (stays, leaves) = FederatedPaths.RemoteLibraryOf(Assert.Single(Stubs, stub => stub.Sources[0].ItemId == "e1").RelativePath) == TestLibraries.Shows
            ? (TestLibraries.Shows, TestLibraries.ShowsB)
            : (TestLibraries.ShowsB, TestLibraries.Shows);
        Give(_anna, _fixture.Path_(series.RelativePath), new UserItemData { Key = "k", IsFavorite = true });
        Apart(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        Assert.Contains(_fixture.Registry.State.Splits, split => split.FromStubId == series.Id);

        Remove(stays, RemoteItemType.Episode, stays == TestLibraries.Shows ? "e1" : "e2");
        Remove(leaves, RemoteItemType.Episode, leaves == TestLibraries.Shows ? "e1" : "e2");
        _fixture.Sync.Client.Add(leaves, RemoteItemType.Episode, TestItems.Episode("new", "s" + leaves, leaves == TestLibraries.Shows ? 1 : 2, 1));
        if (gone)
        {
            Remove(stays, RemoteItemType.Series, "s" + stays);
            Remove(leaves, RemoteItemType.Series, "s" + leaves);
            _fixture.Sync.Client.Items[(leaves, RemoteItemType.Episode)].RemoveAll(item => item.Id == "new");
        }

        await _fixture.SyncAndMaterializeAsync();
        Assert.Equal(!gone, _fixture.Registry.State.Splits.Any(split => split.FromStubId == series.Id));
        Assert.Equal(!gone, _fixture.Registry.State.Series.Any(entry => entry.Id == series.Id));
        if (!gone)
        {
            await ScanAsync();
            Assert.Empty(_fixture.Registry.State.Splits);
            var added = Assert.Single(_fixture.Registry.State.Series, entry => entry.Id != series.Id && entry.RelativePath.Contains("Show (2020)", StringComparison.Ordinal));
            Assert.True(Row(_anna, _fixture.Path_(added.RelativePath)).IsFavorite);
        }
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

    private static void AssertEmpty(UserItemData row)
    {
        Assert.Equal((false, 0, 0L, (DateTime?)null), (row.Played, row.PlayCount, row.PlaybackPositionTicks, row.LastPlayedDate));
        Assert.Equal((false, (double?)null), (row.IsFavorite, row.Rating));
    }

    private static UserItemData WithStreams(UserItemData data)
    {
        data.AudioStreamIndex = 3;
        data.SubtitleStreamIndex = 4;
        return data;
    }

    // A track or a part of an audiobook, of one audio stream.
    private static RemoteItem Sound(string id, string name, string album, string artist, string container) => new()
    {
        Id = id,
        Name = name,
        IndexNumber = 1,
        ProductionYear = 2020,
        Album = album,
        AlbumId = "al",
        Artists = [artist],
        AlbumArtists = [new RemoteNamedItem(artist)],
        MediaSources =
        [
            new RemoteMediaSource
            {
                Id = id,
                Container = container,
                MediaStreams = [new RemoteStreamInfo("Audio", container, null, null, 320_000, null, null, true) { Index = 0 }]
            }
        ]
    };

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

    private string SeriesPathOf(StubEntry stub) => _fixture.Path_(Assert.Single(_fixture.Registry.State.Series, series => series.Id == stub.SeriesStubId).RelativePath);

    // A stub of one episode whose item a scan made.
    private async Task<StubEntry> AddSingleAsync(int number)
    {
        await AddSeriesAsync(("a", TestLibraries.Shows, 1, number));
        Together(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        return StubOf(number, null);
    }

    // The file of a single becomes one of the first two episodes.
    private void Widen() => Replace(2, 480);

    private void Replace(int last, int height)
    {
        Remove(TestLibraries.Shows, RemoteItemType.Episode, "a");
        _fixture.Sync.Client.Add(TestLibraries.Shows, RemoteItemType.Episode, TestItems.Episode("a", "s" + TestLibraries.Shows, 1, 1) with { IndexNumberEnd = last, MediaSources = [TestItems.Source("a", height, 500_000)] });
    }

    // The stub of the first episode, of a file of two and of one, takes both when the libraries part, and its state waits.
    private async Task<StubEntry> SplitAsItWidensAsync()
    {
        await AddSeriesAsync();
        AddDouble("x", TestLibraries.Shows, 480);
        Together(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        _fixture.Sync.Client.Add(TestLibraries.ShowsB, RemoteItemType.Series, TestItems.Series("s" + TestLibraries.ShowsB));
        _fixture.Sync.Client.Add(TestLibraries.ShowsB, RemoteItemType.Episode, TestItems.Episode("b1", "s" + TestLibraries.ShowsB, 1, 1));
        _fixture.Sync.Client.Add(TestLibraries.ShowsB, RemoteItemType.Episode, TestItems.Episode("b2", "s" + TestLibraries.ShowsB, 1, 2));
        await _fixture.SyncAndMaterializeAsync();
        await ScanAsync();
        var first = StubOf(1, null);
        Give(_anna, PathOf(first), Full());
        Apart(TypeFolder.Series, TestLibraries.Shows, TestLibraries.ShowsB);
        await _fixture.SyncAndMaterializeAsync();
        return first;
    }

    // A stub whose state is still of one episode: the split of another from it waits, and its move to a new server name too.
    private Task MarkAsync(StubEntry from, StubEntry taker) => SaveAsync(state => state with
    {
        Stubs = state.Stubs.Select(stub => stub.Id == from.Id ? stub with { StateOfOneUntil = _day.AddDays(1) } : stub).ToList(),
        Splits = [new StubSplit(taker.Id, from.Id)],
        Renames = [new StubRename(from.Id, "old.strm")]
    });

    private Task SaveRenamesAsync(params StubRename[] renames) => SaveAsync(state => state with { Renames = renames });

    private async Task SaveAsync(Func<StubState, StubState> change)
    {
        await _fixture.Registry.EnterAsync(CancellationToken.None);
        try
        {
            await _fixture.Registry.SaveAsync(change(_fixture.Registry.State), CancellationToken.None);
        }
        finally
        {
            _fixture.Registry.Exit();
        }
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

    // One title of a kind as the same item in two remote libraries of its type.
    private async Task<(string First, string Second)> AddTitleAsync(StubKind kind)
    {
        var (first, second, collection) = kind switch
        {
            StubKind.Track => (TestLibraries.Music, MusicB, "music"),
            StubKind.MusicVideo => (TestLibraries.MusicVideos, MusicVideosB, "musicvideos"),
            StubKind.HomeVideo or StubKind.Photo => (TestLibraries.HomeVideos, HomeVideosB, "homevideos"),
            _ => (TestLibraries.Books, BooksB, "books")
        };
        (RemoteItemType Type, RemoteItem Item)[] items = kind switch
        {
            StubKind.Track => [(RemoteItemType.Audio, Sound("t", "Song", "Album", "Artist", "flac"))],
            StubKind.AudioBook => [(RemoteItemType.AudioBook, Sound("p", "Part", "Story", "Narrator", "mp3"))],
            StubKind.MusicVideo => [(RemoteItemType.MusicVideo, new RemoteItem
            {
                Id = "mv",
                Name = "Song",
                ProductionYear = 2019,
                Artists = ["Singer"],
                MediaSources = [TestItems.Source("mv", 1080, 5_000_000)]
            })],
            StubKind.HomeVideo => [(RemoteItemType.Video, new RemoteItem
            {
                Id = "v",
                Name = "Beach day",
                ParentId = first,
                PremiereDate = _day,
                MediaSources = [TestItems.Source("v", 1080, 5_000_000)]
            })],
            StubKind.Photo =>
            [
                (RemoteItemType.PhotoAlbum, new RemoteItem { Id = "al", Name = "Trips", ParentId = first }),
                (RemoteItemType.Photo, new RemoteItem { Id = "ph", Name = "IMG_0001", ParentId = "al", Path = "/home/Trips/IMG_0001.JPG", Width = 4000, Height = 3000 })
            ],
            _ => [(RemoteItemType.Book, new RemoteItem { Id = "bk", Name = "Dune", ProductionYear = 1965, Path = "/books/Dune/Dune.epub" })]
        };
        foreach (var library in new[] { first, second })
        {
            _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(library, library == first ? "Uno" : "Due", collection));
            foreach (var (type, item) in items)
            {
                _fixture.Sync.Client.Add(library, type, item);
            }
        }

        _server = await _fixture.Sync.AddServerAsync("A", first, second);
        return (first, second);
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
        manager.Setup(mock => mock.GetUsers()).Returns(users ?? _users);
        var task = new StubCleanupTask(
            registry ?? _fixture.Registry,
            _fixture.Resolver,
            _fixture.Sync.Index,
            _fixture.Sync.Registry,
            _fixture.Sync.Store,
            _fixture.Library.Object,
            manager.Object,
            _fixture.UserData.Object,
            Mock.Of<IProviderManager>(),
            Mock.Of<IFileSystem>(),
            _fixture.Remover,
            _logger,
            _versions.Object,
            cleaner: _fixture.Cleaner);
        return task.Run(new Progress<double>(), CancellationToken.None);
    }
}
