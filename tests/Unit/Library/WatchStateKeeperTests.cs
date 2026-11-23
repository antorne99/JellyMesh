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
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// Row 412: the watch state follows the content, not the server.
public sealed class WatchStateKeeperTests : IDisposable
{
    private const long NinetyMinutes = 54_000_000_000;

    private static readonly DateTime _watched = new(2026, 10, 1, 20, 0, 0, DateTimeKind.Utc);

    private readonly StubFixture _fixture = new();
    private readonly string _data = Path.Combine(Path.GetTempPath(), "jellymesh-keeper-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Dictionary<string, BaseItem> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly HashSet<(Guid User, Guid Item)> _hidden = [];
    private readonly HashSet<Guid> _failingSaves = [];
    private readonly Dictionary<(Guid User, Guid Item), UserItemData> _rows = [];
    private readonly User _anna = new("anna", "auth", "reset");
    private readonly User _bruno = new("bruno", "auth", "reset");
    private readonly CapturingLogger<WatchStateKeeper> _logger = new();
    private readonly KeptWatchStateStore _kept;
    private readonly WatchStateKeeper _keeper;
    private readonly LibraryMaterializer _materializer;
    private List<User> _everyone;

    public WatchStateKeeperTests()
    {
        Directory.CreateDirectory(_data);
        _everyone = [_anna, _bruno];
        _users.Setup(users => users.GetUsers()).Returns(() => _everyone);
        _userData.Setup(data => data.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((User user, BaseItem item) =>
            {
                if (!_rows.TryGetValue((user.Id, item.Id), out var row))
                {
                    row = new UserItemData { Key = item.Id.ToString("N") };
                    _rows[(user.Id, item.Id)] = row;
                }

                return row;
            });
        _userData.Setup(data => data.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()))
            .Callback((User user, BaseItem item, UserItemData data, UserDataSaveReason reason, CancellationToken _) =>
            {
                if (_failingSaves.Contains(item.Id))
                {
                    throw new IOException("/secret disk full");
                }

                Assert.Equal(UserDataSaveReason.Import, reason);
                _rows[(user.Id, item.Id)] = data;
            });

        var library = _fixture.Library;
        library.Setup(manager => manager.FindByPath(It.IsAny<string>(), It.IsAny<bool?>())).Returns((string path, bool? _) => _byPath.GetValueOrDefault(path));
        library.Setup(manager => manager.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
        library.Setup(manager => manager.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User>()))
            .Returns((Guid id, User user) => _hidden.Contains((user.Id, id)) ? null : _items.GetValueOrDefault(id));
        library.Setup(manager => manager.GetPeople(It.IsAny<BaseItem>())).Returns([]);
        library.Setup(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()))
            .Callback((BaseItem item, DeleteOptions _) =>
            {
                _items.Remove(item.Id);
                _byPath.Remove(item.Path);
            });

        _kept = new KeptWatchStateStore(_fixture.Database);
        _keeper = Keeper(_kept);
        _materializer = MaterializerWith(_keeper);
    }

    public void Dispose()
    {
        _keeper.Dispose();
        _fixture.Dispose();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, true);
        }
    }

    private static RemoteItem FilmWithoutIds(string id = "nf", long runtime = NinetyMinutes) =>
        TestItems.Movie(id, "Sonda Senza ID", 2026) with { ProviderIds = new Dictionary<string, string?>(), RunTimeTicks = runtime };

    private WatchStateKeeper Keeper(IKeptWatchStateStore store, IManualMergeStore? merges = null) => new(
        _fixture.Registry,
        _fixture.Sync.Index,
        _fixture.Sync.Registry,
        _fixture.Sync.Store,
        _fixture.Library.Object,
        _users.Object,
        _userData.Object,
        store,
        merges ?? _fixture.Merges,
        _fixture.Sync.Clock,
        _logger);

    private LibraryMaterializer MaterializerWith(IWatchStateKeeper keeper) => new(
        _fixture.Registry,
        _fixture.Hooked,
        _fixture.Sync.Index,
        _fixture.Sync.Registry,
        _fixture.Sync.Client,
        _fixture.Sync.Store,
        _fixture.Library.Object,
        _fixture.Updater,
        _fixture.Technical,
        _fixture.Remover,
        new StubUrlBuilder(_fixture.Host.Object, _fixture.Network.Object, _fixture.Signer),
        _fixture.Sync.Clock,
        _fixture.Logger,
        _fixture.Refresher,
        _fixture.Linker.Object,
        keeper);

    private CatalogSyncService SyncWith(LibraryMaterializer materializer) => new(
        _fixture.Sync.Registry,
        _fixture.Sync.Reader,
        _fixture.Sync.Index,
        _fixture.Sync.Vault,
        _fixture.Sync.Clock,
        _fixture.Sync.Logger,
        materializer,
        _fixture.Registry);

    private async Task RemoveAsync(RemoteServerInfo server, LibraryMaterializer? materializer = null)
    {
        using var sync = SyncWith(materializer ?? _materializer);
        await sync.RemoveServerAsync(server.Id, () => _fixture.Sync.Registry.RemoveAsync(server.Id), CancellationToken.None);
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);
    }

    private StubEntry StubOf(RemoteServerInfo server) => _fixture.Registry.State.Stubs.Single(stub => stub.ServerId == server.Id);

    // The library item a scan makes of a film stub, with the metadata of its .nfo.
    private Movie FilmItem(RemoteServerInfo server, long runtime = NinetyMinutes) => FilmItem(server, null, runtime);

    private Movie FilmItem(RemoteServerInfo server, string? kind, long runtime = NinetyMinutes)
    {
        var stub = _fixture.Registry.State.Stubs.Single(stub => stub.ServerId == server.Id && (kind is null || stub.Kind == StubKind.Movie));
        var item = new Movie
        {
            Id = Guid.NewGuid(),
            Path = FederatedPaths.Resolve(_fixture.Root, stub.RelativePath),
            Name = "Sonda Senza ID",
            ProductionYear = 2026,
            RunTimeTicks = runtime
        };
        Add(item);
        return item;
    }

    private static RemoteItem Clip(string id, string? imdb, string artist = "Singer") => new()
    {
        Id = id,
        Name = "Song",
        ProductionYear = 2019,
        Artists = [artist],
        ProviderIds = imdb is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["Imdb"] = imdb },
        MediaSources = [TestItems.Source(id, 1080, 5_000_000)]
    };

    // The library item a scan makes of a music video stub.
    private MediaBrowser.Controller.Entities.MusicVideo ClipItem(RemoteServerInfo server, string? imdb = null)
    {
        var stub = _fixture.Registry.State.Stubs.Single(stub => stub.ServerId == server.Id && stub.Kind == StubKind.MusicVideo);
        var item = new MediaBrowser.Controller.Entities.MusicVideo
        {
            Id = Guid.NewGuid(),
            Path = FederatedPaths.Resolve(_fixture.Root, stub.RelativePath),
            Name = "Song",
            ProductionYear = 2019,
            Artists = ["Singer"],
            ProviderIds = imdb is null ? [] : new Dictionary<string, string> { ["Imdb"] = imdb }
        };
        Add(item);
        return item;
    }

    private void Add(BaseItem item)
    {
        _items[item.Id] = item;
        _byPath[item.Path] = item;
    }

    private UserItemData Row(User user, BaseItem item) => _userData.Object.GetUserData(user, item)!;

    private void Watch(User user, BaseItem item, int count = 2, DateTime? when = null)
    {
        var row = Row(user, item);
        row.Played = true;
        row.PlayCount = count;
        row.LastPlayedDate = when ?? _watched;
    }

    private void Resume(User user, BaseItem item, long position)
    {
        var row = Row(user, item);
        row.PlaybackPositionTicks = position;
        row.LastPlayedDate = _watched;
        row.IsFavorite = true;
    }

    private async Task<RemoteServerInfo> AddAndMaterializeAsync(string name, string library)
    {
        var server = await _fixture.Sync.AddServerAsync(name, library);
        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);
        await _materializer.MaterializeAsync(CancellationToken.None);
        return server;
    }

    [Fact]
    public async Task AFilmWithoutIdsGetsItsStateBackWhenItsServerIsAddedAgain()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        var old = FilmItem(a);
        Watch(_anna, old);
        Resume(_bruno, old, 9_000_000_000);

        await RemoveAsync(a);

        Assert.Empty(_fixture.Registry.State.Stubs);
        var kept = Assert.Single(await _kept.ReadAsync(CancellationToken.None));
        Assert.Equal(old.Id, kept.OldItemId);
        Assert.Equal(2, kept.Users.Count);

        // Added again it is a new server (row 408), and its stub has no item until the scan.
        var again = await AddAndMaterializeAsync("A", TestLibraries.Films);
        await _keeper.RestoreAsync(CancellationToken.None);
        Assert.Single(await _kept.ReadAsync(CancellationToken.None));

        var fresh = FilmItem(again);
        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.True(Row(_anna, fresh).Played);
        Assert.Equal(2, Row(_anna, fresh).PlayCount);
        Assert.Equal(_watched, Row(_anna, fresh).LastPlayedDate);
        Assert.Equal(9_000_000_000, Row(_bruno, fresh).PlaybackPositionTicks);
        Assert.True(Row(_bruno, fresh).IsFavorite);
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AMusicVideoWithoutIdsGetsItsStateBackByItsArtistsTitleAndYear()
    {
        _fixture.Sync.Client.Libraries.Add(new(TestLibraries.MusicVideos, "Clip", "musicvideos"));
        _fixture.Sync.Client.Add(TestLibraries.MusicVideos, RemoteItemType.MusicVideo, Clip("mv", null));
        var a = await AddAndMaterializeAsync("A", TestLibraries.MusicVideos);
        Watch(_anna, ClipItem(a));

        await RemoveAsync(a);

        var kept = Assert.Single(await _kept.ReadAsync(CancellationToken.None));
        Assert.Equal(KeptTitleKind.MusicVideo, kept.Kind);
        Assert.Equal(["Singer"], kept.Artists);

        var fresh = ClipItem(await AddAndMaterializeAsync("A", TestLibraries.MusicVideos));
        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.True(Row(_anna, fresh).Played);
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AKeptMusicVideoGivesNothingToAFilmWithTheSameId()
    {
        _fixture.Sync.Client.Libraries.Add(new(TestLibraries.MusicVideos, "Clip", "musicvideos"));
        _fixture.Sync.Client.Add(TestLibraries.MusicVideos, RemoteItemType.MusicVideo, Clip("mv", "tt1"));
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("film", "Song", 2019) with { ProviderIds = new Dictionary<string, string?> { ["Imdb"] = "tt1" } });
        var a = await AddAndMaterializeAsync("A", TestLibraries.MusicVideos);
        Watch(_anna, ClipItem(a, "tt1"));
        await RemoveAsync(a);
        Assert.Equal("tt1", Assert.Single(await _kept.ReadAsync(CancellationToken.None)).Ids.Imdb);

        var film = FilmItem(await AddAndMaterializeAsync("B", TestLibraries.Films));
        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.False(Row(_anna, film).Played);
        Assert.Single(await _kept.ReadAsync(CancellationToken.None));
    }

    // A remote title of a type with a kept kind of its own, by the name, year and number all of them share.
    private void AddRemote(StubKind kind, string? id = null, int copies = 1)
    {
        var (library, type, item) = kind switch
        {
            StubKind.Track => (new RemoteLibrary(TestLibraries.Music, "Dischi", "music"), RemoteItemType.Audio, Sound("t1") with
            {
                ProviderIds = id is null ? null : new Dictionary<string, string?> { ["MusicBrainzRecording"] = id }
            }),
            StubKind.AudioBook => (new RemoteLibrary(TestLibraries.Books, "Libri", "books"), RemoteItemType.AudioBook, Sound("p1")),
            StubKind.Book => (new RemoteLibrary(TestLibraries.Books, "Libri", "books"), RemoteItemType.Book, new RemoteItem
            {
                Id = "b1",
                Name = "Song",
                ProductionYear = 2019,
                SeriesName = "Saga",
                Path = "/books/Song/Song.epub",
                ProviderIds = id is null ? null : new Dictionary<string, string?> { ["ISBN"] = id }
            }),
            StubKind.HomeVideo => (new RemoteLibrary(TestLibraries.HomeVideos, "Casa", "homevideos"), RemoteItemType.Video, new RemoteItem
            {
                Id = "v1",
                Name = "Song",
                ProductionYear = 2019,
                ParentId = TestLibraries.HomeVideos,
                RunTimeTicks = 180 * TimeSpan.TicksPerSecond,
                MediaSources = [TestItems.Source("v1", 1080, 5_000_000)]
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        _fixture.Sync.Client.Libraries.Add(library);
        _fixture.Sync.Client.Add(library.Id, type, item);
        _fixture.Sync.Client.Add(library.Id, type, [.. Enumerable.Range(1, copies - 1).Select(copy => item with { Id = item.Id + "c" + copy })]);
    }

    private static RemoteItem Sound(string id) => new()
    {
        Id = id,
        Name = "Song",
        ProductionYear = 2019,
        Album = "Album",
        AlbumId = "al1",
        Artists = ["Singer"],
        AlbumArtists = [new RemoteNamedItem("Singer")],
        ParentIndexNumber = 2,
        IndexNumber = 3,
        RunTimeTicks = 180 * TimeSpan.TicksPerSecond,
        MediaSources = [new RemoteMediaSource { Id = id, Container = "mp3", MediaStreams = [new RemoteStreamInfo("Audio", "mp3", null, null, 320_000, null, null, true) { Index = 0 }] }]
    };

    private static string LibraryOf(StubKind kind) => kind switch
    {
        StubKind.Track => TestLibraries.Music,
        StubKind.HomeVideo => TestLibraries.HomeVideos,
        _ => TestLibraries.Books
    };

    // The library item a scan makes of the only stub of a kind, with the name, year and numbers all of them share.
    private BaseItem ItemOf(RemoteServerInfo server, StubKind kind) => Place(NewItem(kind), server, kind);

    private static BaseItem NewItem(StubKind kind)
    {
        BaseItem item = kind switch
        {
            StubKind.Track => new MediaBrowser.Controller.Entities.Audio.Audio { Artists = ["Singer"], AlbumArtists = ["Singer"], Album = "Album" },
            StubKind.AudioBook => new AudioBook { Artists = ["Singer"], AlbumArtists = ["Singer"], Album = "Album" },
            StubKind.Book => new Book { SeriesName = "Saga" },
            _ => new Video()
        };
        item.Name = "Song";
        item.ProductionYear = 2019;
        item.ParentIndexNumber = 2;
        item.IndexNumber = 3;
        item.RunTimeTicks = 181 * TimeSpan.TicksPerSecond;
        return item;
    }

    private BaseItem Place(BaseItem item, RemoteServerInfo server, StubKind kind)
    {
        item.Id = Guid.NewGuid();
        item.Path = FederatedPaths.Resolve(_fixture.Root, _fixture.Registry.State.Stubs.Single(stub => stub.ServerId == server.Id && stub.Kind == kind).RelativePath);
        Add(item);
        return item;
    }

    [Theory]
    [InlineData(StubKind.Movie, KeptTitleKind.Movie)]
    [InlineData(StubKind.Episode, KeptTitleKind.Episode)]
    [InlineData(StubKind.MusicVideo, KeptTitleKind.MusicVideo)]
    [InlineData(StubKind.Track, KeptTitleKind.Track)]
    [InlineData(StubKind.AudioBook, KeptTitleKind.AudioBook)]
    [InlineData(StubKind.Book, KeptTitleKind.Book)]
    [InlineData(StubKind.HomeVideo, KeptTitleKind.HomeVideo)]
    public void EachKindOfStubIsKeptUnderAKindOfItsOwn(StubKind kind, KeptTitleKind kept) => Assert.Equal(kept, WatchStateKeeper.KeptKindOf(kind));

    [Fact]
    public void EveryKindOfStubHasAKindToBeKeptUnder()
    {
        var kinds = Enum.GetValues<StubKind>();

        Assert.Equal(kinds.Length, kinds.Select(WatchStateKeeper.KeptKindOf).Distinct().Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => WatchStateKeeper.KeptKindOf((StubKind)(-1)));
    }

    // Row 532: never as a film, with everything a rule of identity may read and no mark of the plugin.
    [Theory]
    [InlineData(StubKind.Track, KeptTitleKind.Track, "Singer/Album/2-03 - Song.strm")]
    [InlineData(StubKind.AudioBook, KeptTitleKind.AudioBook, "Singer/Album/2-03 - Song.strm")]
    [InlineData(StubKind.Book, KeptTitleKind.Book, "Song (2019)/Song (2019).epub")]
    [InlineData(StubKind.HomeVideo, KeptTitleKind.HomeVideo, "Song.strm")]
    public async Task ATitleOfATypeOfItsOwnIsKeptUnderItsKindWithItsData(StubKind kind, KeptTitleKind expected, string place)
    {
        AddRemote(kind);
        var a = await AddAndMaterializeAsync("A", LibraryOf(kind));
        var item = ItemOf(a, kind);
        item.ProviderIds = new Dictionary<string, string> { ["Isbn"] = "978", ["JellyMesh"] = "stub" };
        Watch(_anna, item);

        await RemoveAsync(a);

        var kept = Assert.Single(await _kept.ReadAsync(CancellationToken.None));
        Assert.Equal((expected, "Song", 2019, 2, 3), (kept.Kind, kept.Title, kept.Year, kept.SeasonNumber, kept.EpisodeNumber));
        Assert.Equal(181 * TimeSpan.TicksPerSecond, kept.RunTimeTicks);
        Assert.Equal(new Dictionary<string, string> { ["Isbn"] = "978" }, kept.ProviderIds);
        Assert.Equal(place, kept.Place);
        Assert.Equal(kind == StubKind.Book ? "Saga" : null, kept.SeriesName);
        var isSound = kind is StubKind.Track or StubKind.AudioBook;
        Assert.Equal(isSound ? "Album" : null, kept.Album);
        Assert.Equal(isSound ? ["Singer"] : [], kept.Artists);
        Assert.Equal(isSound ? ["Singer"] : [], kept.AlbumArtists);
        Assert.Equal((_anna.Id, 2), (Assert.Single(kept.Users).UserId, kept.Users[0].PlayCount));
    }

    // The library the admin put a server folder in decides the type of the item, not what it is.
    [Fact]
    public async Task AHomeVideoReadAsAFilmIsKeptAsAHomeVideoAndAFilmReadAsAMusicVideoAsAFilm()
    {
        AddRemote(StubKind.HomeVideo);
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("film", "Film", 2019));
        var a = await _fixture.Sync.AddServerAsync("A", TestLibraries.HomeVideos, TestLibraries.Films);
        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);
        await _materializer.MaterializeAsync(CancellationToken.None);
        Watch(_anna, Place(new Movie { Name = "Song" }, a, StubKind.HomeVideo));
        Watch(_anna, Place(new MusicVideo { Name = "Film" }, a, StubKind.Movie));

        await RemoveAsync(a);

        var kept = await _kept.ReadAsync(CancellationToken.None);
        Assert.Equal([("Film", KeptTitleKind.Movie), ("Song", KeptTitleKind.HomeVideo)], kept.Select(title => (title.Title!, title.Kind)).Order());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATrackGetsItsStateBackByItsRecordingOrItsPlaceInTheAlbum(bool byRecording)
    {
        AddRemote(StubKind.Track, byRecording ? "rec-1" : null);
        var a = await AddAndMaterializeAsync("A", TestLibraries.Music);
        var old = ItemOf(a, StubKind.Track);
        if (byRecording)
        {
            old.Name = "Other name";
            old.ProviderIds = new Dictionary<string, string> { ["musicbrainzrecording"] = "REC-1" };
        }

        Watch(_anna, old);
        Resume(_bruno, old, 900_000_000);
        await RemoveAsync(a);
        Assert.Single(await _kept.ReadAsync(CancellationToken.None));

        var fresh = ItemOf(await AddAndMaterializeAsync("A", TestLibraries.Music), StubKind.Track);
        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.Equal((true, 2), (Row(_anna, fresh).Played, Row(_anna, fresh).PlayCount));
        Assert.Equal((900_000_000, true), (Row(_bruno, fresh).PlaybackPositionTicks, Row(_bruno, fresh).IsFavorite));
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EveryServerThatHasTheTrackGetsItsState()
    {
        AddRemote(StubKind.Track);
        var a = await AddAndMaterializeAsync("A", TestLibraries.Music);
        var b = ItemOf(await AddAndMaterializeAsync("B", TestLibraries.Music), StubKind.Track);
        var c = ItemOf(await AddAndMaterializeAsync("C", TestLibraries.Music), StubKind.Track);
        Watch(_anna, ItemOf(a, StubKind.Track));

        await RemoveAsync(a);

        Assert.True(Row(_anna, b).Played);
        Assert.True(Row(_anna, c).Played);
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnotherRecordingOfATrackGetsNothing()
    {
        AddRemote(StubKind.Track, "rec-2");
        var a = await AddAndMaterializeAsync("A", TestLibraries.Music);
        var old = ItemOf(a, StubKind.Track);
        old.ProviderIds = new Dictionary<string, string> { ["MusicBrainzRecording"] = "rec-1" };
        Watch(_anna, old);
        await RemoveAsync(a);

        var fresh = ItemOf(await AddAndMaterializeAsync("B", TestLibraries.Music), StubKind.Track);
        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.False(Row(_anna, fresh).Played);
        Assert.Single(await _kept.ReadAsync(CancellationToken.None));
    }

    // Row 535: an audiobook as a track, a book by its ID or its data, a home video by its data.
    [Theory]
    [InlineData(StubKind.AudioBook)]
    [InlineData(StubKind.Book)]
    [InlineData(StubKind.HomeVideo)]
    public async Task AnAudioBookABookAndAHomeVideoGetTheirStateBack(StubKind kind)
    {
        AddRemote(kind);
        var a = await AddAndMaterializeAsync("A", LibraryOf(kind));
        var b = ItemOf(await AddAndMaterializeAsync("B", LibraryOf(kind)), kind);
        var old = ItemOf(a, kind);
        Watch(_anna, old);
        await RemoveAsync(a);
        Assert.True(Row(_anna, b).Played);
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(StubKind.AudioBook)]
    [InlineData(StubKind.Book)]
    [InlineData(StubKind.HomeVideo)]
    public async Task AnAudioBookABookAndAHomeVideoThatAreNotTheSameGetNothing(StubKind kind)
    {
        AddRemote(kind);
        var a = await AddAndMaterializeAsync("A", LibraryOf(kind));
        var b = ItemOf(await AddAndMaterializeAsync("B", LibraryOf(kind)), kind);
        var old = ItemOf(a, kind);
        old.Name = "Another";
        Watch(_anna, old);

        await RemoveAsync(a);

        Assert.False(Row(_anna, b).Played);
        Assert.Single(await _kept.ReadAsync(CancellationToken.None));
    }

    // Row 542: a title with more copies than the most keeps its state, one line says so, and the others get theirs.
    [Theory]
    [InlineData(StubKind.Movie, 100, true)]
    [InlineData(StubKind.Movie, 101, false)]
    [InlineData(StubKind.AudioBook, 100, true)]
    [InlineData(StubKind.AudioBook, 101, false)]
    [InlineData(StubKind.Book, 101, false)]
    [InlineData(StubKind.HomeVideo, 101, false)]
    public async Task ATitleWithMoreCopiesThanTheMostKeepsItsState(StubKind kind, int copies, bool isPutBack)
    {
        var film = kind == StubKind.Movie;
        if (film)
        {
            _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, [.. Enumerable.Range(1, copies).Select(copy => TestItems.Movie("film" + copy, "Song", 2019) with { ProviderIds = new Dictionary<string, string?> { ["Imdb"] = "tt1" } })]);
        }
        else
        {
            AddRemote(kind, copies: copies);
        }

        var b = await AddAndMaterializeAsync("B", film ? TestLibraries.Films : LibraryOf(kind));
        var placed = _fixture.Registry.State.Stubs.Where(stub => stub.ServerId == b.Id && stub.Kind == kind).Select(stub =>
        {
            var item = film ? new Movie { Name = "Song", ProductionYear = 2019 } : NewItem(kind);
            item.Id = Guid.NewGuid();
            item.Path = FederatedPaths.Resolve(_fixture.Root, stub.RelativePath);
            Add(item);
            return item;
        }).ToList();
        await _kept.SaveAsync([KeptOf(kind)], CancellationToken.None);

        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.Equal(isPutBack, placed.Exists(item => Row(_anna, item).Played));
        Assert.Equal(isPutBack ? 0 : 1, (await _kept.ReadAsync(CancellationToken.None)).Count);
        Assert.Equal(isPutBack ? 0 : 1, _logger.Lines.Count(line => line.Contains("more than 100 copies", StringComparison.Ordinal)));
    }

    // A rule that fails leaves the titles of its type for a later run and stops no other.
    [Fact]
    public async Task TheFailureOfOneRuleDoesNotStopTheOthers()
    {
        AddRemote(StubKind.Track);
        AddRemote(StubKind.Book);
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("film", "Song", 2019) with { ProviderIds = new Dictionary<string, string?> { ["Imdb"] = "tt1" } });
        var b = await _fixture.Sync.AddServerAsync("B", TestLibraries.Films, TestLibraries.Music, TestLibraries.Books);
        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);
        await _materializer.MaterializeAsync(CancellationToken.None);
        var (film, track, book) = (Place(new Movie { Name = "Song", ProductionYear = 2019 }, b, StubKind.Movie), ItemOf(b, StubKind.Track), ItemOf(b, StubKind.Book));
        await _kept.SaveAsync([KeptOf(StubKind.Movie), KeptOf(StubKind.Track), KeptOf(StubKind.Book)], CancellationToken.None);
        var broken = new Mock<IManualMergeStore>();
        broken.Setup(merges => merges.Merges).Throws(new InvalidOperationException("/secret"));
        using var keeper = Keeper(_kept, broken.Object);

        await keeper.RestoreAsync(CancellationToken.None);

        Assert.True(Row(_anna, track).Played);
        Assert.True(Row(_anna, book).Played);
        Assert.False(Row(_anna, film).Played);
        Assert.Equal(KeptTitleKind.Movie, Assert.Single(await _kept.ReadAsync(CancellationToken.None)).Kind);
        Assert.Contains("InvalidOperationException", Assert.Single(_logger.Lines, line => line.Contains("Cannot match", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.DoesNotContain(_logger.Lines, line => line.Contains("secret", StringComparison.Ordinal));
    }

    // A kept title as the keeper saves the item of a stub of a kind, seen by Anna.
    private KeptTitle KeptOf(StubKind kind) => new()
    {
        OldItemId = Guid.NewGuid(),
        Kind = WatchStateKeeper.KeptKindOf(kind),
        Title = "Song",
        Year = 2019,
        Ids = kind == StubKind.Movie ? new ContentIds { Imdb = "tt1" } : new ContentIds(),
        AlbumArtists = ["Singer"],
        Album = "Album",
        SeriesName = kind == StubKind.Book ? "Saga" : null,
        Place = kind == StubKind.Book ? "Song.epub" : "Song.strm",
        SeasonNumber = 2,
        EpisodeNumber = 3,
        RunTimeTicks = 180 * TimeSpan.TicksPerSecond,
        Users = [new KeptUserState(_anna.Id, true, 2, 0, _watched, false, null)],
        SavedUtc = _fixture.Sync.Clock.Now.UtcDateTime
    };

    [Fact]
    public async Task ABookWithTheSameIdGetsTheStateWhateverItsTitle()
    {
        AddRemote(StubKind.Book, "978");
        var a = await AddAndMaterializeAsync("A", TestLibraries.Books);
        var b = ItemOf(await AddAndMaterializeAsync("B", TestLibraries.Books), StubKind.Book);
        var old = ItemOf(a, StubKind.Book);
        old.Name = "Another";
        old.ProviderIds = new Dictionary<string, string> { ["ISBN"] = "978", ["JellyMesh"] = "one" };
        Watch(_anna, old);

        await RemoveAsync(a);

        Assert.True(Row(_anna, b).Played);
    }

    // The only file of a book needs no number; a file of a longer book does.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task AnAudioBookWithNoNumberGetsItsStateBackOnlyWhenItIsOneFile(bool isLonger, bool isRestored)
    {
        AddRemote(StubKind.AudioBook);
        if (isLonger)
        {
            _fixture.Sync.Client.Add(TestLibraries.Books, RemoteItemType.AudioBook, Sound("p2") with { Name = "Second", IndexNumber = 4 });
        }

        var a = await AddAndMaterializeAsync("A", TestLibraries.Books);
        var b = await AddAndMaterializeAsync("B", TestLibraries.Books);
        BaseItem First(RemoteServerInfo server)
        {
            var item = new AudioBook { Id = Guid.NewGuid(), Name = "Song", Artists = ["Singer"], AlbumArtists = ["Singer"], Album = "Album", ParentIndexNumber = 2, RunTimeTicks = 181 * TimeSpan.TicksPerSecond };
            item.Path = FederatedPaths.Resolve(_fixture.Root, _fixture.Registry.State.Stubs.Single(stub => stub.ServerId == server.Id && stub.Sources[0].ItemId == "p1").RelativePath);
            Add(item);
            return item;
        }

        var other = First(b);
        Watch(_anna, First(a));
        await RemoveAsync(a);

        Assert.Equal(isRestored, Row(_anna, other).Played);
    }

    // An audiobook and a track with the same data are not one another.
    [Theory]
    [InlineData(StubKind.Track, StubKind.AudioBook)]
    [InlineData(StubKind.AudioBook, StubKind.Track)]
    public async Task ATrackAndAnAudioBookNeverShareTheirState(StubKind kept, StubKind other)
    {
        AddRemote(kept);
        AddRemote(other);
        var a = await AddAndMaterializeAsync("A", LibraryOf(kept));
        var b = ItemOf(await AddAndMaterializeAsync("B", LibraryOf(other)), other);
        Watch(_anna, ItemOf(a, kept));

        await RemoveAsync(a);

        Assert.False(Row(_anna, b).Played);
        Assert.Single(await _kept.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(StubKind.Track)]
    [InlineData(StubKind.AudioBook)]
    [InlineData(StubKind.Book)]
    [InlineData(StubKind.HomeVideo)]
    public async Task ATitleOfATypeOfItsOwnGivesNothingToAFilmAnEpisodeOrASeriesOfTheSameName(StubKind kind)
    {
        var ids = new Dictionary<string, string?> { ["Imdb"] = "tt1" };
        AddRemote(kind);
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("film", "Song", 2019) with { ProviderIds = ids });
        _fixture.Sync.Client.Add(TestLibraries.Shows, RemoteItemType.Series, TestItems.Series("s", "Song", 2019) with { ProviderIds = ids });
        _fixture.Sync.Client.Add(TestLibraries.Shows, RemoteItemType.Season, TestItems.Season("ss2", "s", 2));
        _fixture.Sync.Client.Add(TestLibraries.Shows, RemoteItemType.Episode, TestItems.Episode("e", "s", 2, 3, "Song") with { SeasonId = "ss2" });
        var a = await AddAndMaterializeAsync("A", LibraryOf(kind));
        var old = ItemOf(a, kind);
        old.ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt1" };
        Watch(_anna, old);
        await RemoveAsync(a);

        var b = await _fixture.Sync.AddServerAsync("B", TestLibraries.Films, TestLibraries.Shows);
        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);
        await _materializer.MaterializeAsync(CancellationToken.None);
        var film = Place(new Movie { Name = "Song", ProductionYear = 2019 }, b, StubKind.Movie);
        var series = new Series { Id = Guid.NewGuid(), Path = FederatedPaths.Resolve(_fixture.Root, Assert.Single(_fixture.Registry.State.Series).RelativePath), Name = "Song", ProductionYear = 2019 };
        Add(series);
        var episode = Place(new Episode { SeriesId = series.Id, ParentIndexNumber = 2, IndexNumber = 3 }, b, StubKind.Episode);
        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.All([film, series, episode], item => Assert.False(Row(_anna, item).Played));
        Assert.Single(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheStateGoesRightAwayToAServerThatAlreadyHasTheContent()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        var b = await AddAndMaterializeAsync("B", TestLibraries.Films);
        var old = FilmItem(a);
        var other = FilmItem(b);
        Watch(_anna, old);

        await RemoveAsync(a);

        Assert.True(Row(_anna, other).Played);
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EveryServerThatHasTheContentGetsTheState()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        _fixture.Sync.Client.Add(TestLibraries.Shows, RemoteItemType.Series, TestItems.Series("s", "Serie Senza ID", 2026) with { ProviderIds = new Dictionary<string, string?>() });
        _fixture.Sync.Client.Add(TestLibraries.Shows, RemoteItemType.Season, TestItems.Season("ss1", "s", 1));
        _fixture.Sync.Client.Add(TestLibraries.Shows, RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2, "Serie Senza ID") with { SeasonId = "ss1" });
        var a = await _fixture.Sync.AddServerAsync("A", TestLibraries.Films, TestLibraries.Shows);
        var b = await _fixture.Sync.AddServerAsync("B", TestLibraries.Films, TestLibraries.Shows);
        var c = await _fixture.Sync.AddServerAsync("C", TestLibraries.Films, TestLibraries.Shows);
        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);
        await _materializer.MaterializeAsync(CancellationToken.None);
        var old = FilmItem(a, "Film");
        var (oldSeries, oldEpisode) = ShowItems(a);
        Watch(_anna, old);
        Watch(_anna, oldEpisode);
        Row(_anna, oldSeries).IsFavorite = true;
        var films = new[] { FilmItem(b, "Film"), FilmItem(c, "Film") };
        var shows = new[] { ShowItems(b), ShowItems(c) };

        await RemoveAsync(a);

        Assert.All(films, film => Assert.True(Row(_anna, film).Played));
        Assert.All(shows, show => Assert.True(Row(_anna, show.Episode).Played));
        Assert.All(shows, show => Assert.True(Row(_anna, show.Series).IsFavorite));
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheRecordsOfTwoRemovedServersBothComeBack()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        var b = await AddAndMaterializeAsync("B", TestLibraries.Films);
        Watch(_anna, FilmItem(a), 1, _watched);
        Row(_bruno, FilmItem(b)).IsFavorite = true;
        using (var sync = SyncWith(_materializer))
        {
            await sync.RemoveServerAsync(a.Id, () => _fixture.Sync.Registry.RemoveAsync(a.Id), CancellationToken.None);
            await sync.RemoveServerAsync(b.Id, () => _fixture.Sync.Registry.RemoveAsync(b.Id), CancellationToken.None);
            await sync.CleanUpAfterRemovalAsync(CancellationToken.None);
        }

        Assert.Equal(2, (await _kept.ReadAsync(CancellationToken.None)).Count);

        var c = await AddAndMaterializeAsync("C", TestLibraries.Films);
        var fresh = FilmItem(c);
        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.True(Row(_anna, fresh).Played);
        Assert.True(Row(_bruno, fresh).IsFavorite);
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AFilmWithoutIdsWhoseMetadataDiffersGetsNothing()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        Watch(_anna, FilmItem(a));
        await RemoveAsync(a);
        _fixture.Sync.Client.Items[(TestLibraries.Films, RemoteItemType.Movie)].Clear();
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds("other", NinetyMinutes * 2));

        var b = await AddAndMaterializeAsync("B", TestLibraries.Films);
        var other = FilmItem(b, NinetyMinutes * 2);
        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.False(Row(_anna, other).Played);
        Assert.Single(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ANewerStateOnTheNewCopyWinsAndAFavoriteIsAdded()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        var old = FilmItem(a);
        Watch(_anna, old);
        Row(_anna, old).IsFavorite = true;
        await RemoveAsync(a);
        var b = await AddAndMaterializeAsync("B", TestLibraries.Films);
        var fresh = FilmItem(b);
        Row(_anna, fresh).PlaybackPositionTicks = 5;
        Row(_anna, fresh).LastPlayedDate = _watched.AddDays(1);

        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.False(Row(_anna, fresh).Played);
        Assert.Equal(5, Row(_anna, fresh).PlaybackPositionTicks);
        Assert.True(Row(_anna, fresh).IsFavorite);
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AUserWhoDoesNotSeeTheNewCopyKeepsTheRecord()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        var old = FilmItem(a);
        Watch(_anna, old);
        Watch(_bruno, old);
        await RemoveAsync(a);
        var b = await AddAndMaterializeAsync("B", TestLibraries.Films);
        var fresh = FilmItem(b);
        _hidden.Add((_bruno.Id, fresh.Id));

        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.True(Row(_anna, fresh).Played);
        Assert.False(Row(_bruno, fresh).Played);
        Assert.Equal(_bruno.Id, Assert.Single(Assert.Single(await _kept.ReadAsync(CancellationToken.None)).Users).UserId);
    }

    [Fact]
    public async Task ARowThatCannotBeSavedStaysForALaterRun()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        Watch(_anna, FilmItem(a));
        await RemoveAsync(a);
        var b = await AddAndMaterializeAsync("B", TestLibraries.Films);
        var fresh = FilmItem(b);
        _failingSaves.Add(fresh.Id);

        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.Single(await _kept.ReadAsync(CancellationToken.None));
        Assert.DoesNotContain(_logger.Lines, message => message.Contains("secret", StringComparison.Ordinal));

        _failingSaves.Clear();
        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.True(Row(_anna, fresh).Played);
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ARecordNobodyUsedGoesAfter90Days()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        Watch(_anna, FilmItem(a));
        await RemoveAsync(a);

        _fixture.Sync.Clock.Now = _fixture.Sync.Clock.Now.AddDays(89);
        await _keeper.RestoreAsync(CancellationToken.None);
        Assert.Single(await _kept.ReadAsync(CancellationToken.None));

        _fixture.Sync.Clock.Now = _fixture.Sync.Clock.Now.AddDays(2);
        await _keeper.RestoreAsync(CancellationToken.None);
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheRowsOfAUserWhoIsGoneAreForgotten()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        var old = FilmItem(a);
        Watch(_anna, old);
        Watch(_bruno, old);
        await RemoveAsync(a);

        _everyone = [_anna];
        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.Equal(_anna.Id, Assert.Single(Assert.Single(await _kept.ReadAsync(CancellationToken.None)).Users).UserId);
    }

    [Fact]
    public async Task WithoutUsersTheStubsOfARemovedServerStayUntilTheirStateIsSaved()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        var old = FilmItem(a);
        Watch(_anna, old);
        var path = old.Path;
        _everyone = [];

        await RemoveAsync(a);

        Assert.True(File.Exists(path));
        Assert.Single(_fixture.Registry.State.Stubs);
        Assert.Equal(0, _fixture.Database.Count("kept_titles"));

        _everyone = [_anna, _bruno];
        using var sync = SyncWith(_materializer);
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);

        Assert.False(File.Exists(path));
        Assert.Empty(_fixture.Registry.State.Stubs);
        Assert.Single(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AStoreThatFailsHoldsTheRemovalAndTheServerFolder()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        Watch(_anna, FilmItem(a));
        var failing = new Mock<IKeptWatchStateStore>();
        failing.Setup(store => store.SaveAsync(It.IsAny<IReadOnlyCollection<KeptTitle>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("/secret"));
        using var keeper = Keeper(failing.Object);

        await RemoveAsync(a, MaterializerWith(keeper));

        Assert.Single(_fixture.Registry.State.Stubs);
        Assert.True(Directory.Exists(_fixture.Path_(a)));
        Assert.DoesNotContain(_logger.Lines, message => message.Contains("secret", StringComparison.Ordinal));
    }

    // Row 416: the page names each removed server whose removal waits for the save, until the save succeeds.
    [Fact]
    public async Task AHeldRemovalIsListedByNameUntilTheSaveSucceeds()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("Casa di <b>Anna</b>", TestLibraries.Films);
        Watch(_anna, FilmItem(a));
        var fails = true;
        var store = new Mock<IKeptWatchStateStore>();
        store.Setup(kept => kept.ReadMergesAsync(It.IsAny<CancellationToken>())).Returns<CancellationToken>(_kept.ReadMergesAsync);
        store.Setup(kept => kept.SaveAsync(It.IsAny<IReadOnlyCollection<KeptTitle>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyCollection<KeptTitle> titles, CancellationToken token) => fails ? throw new IOException("/secret") : _kept.SaveAsync(titles, token));
        using var keeper = Keeper(store.Object);
        Assert.Empty(keeper.HeldRemovals);

        await RemoveAsync(a, MaterializerWith(keeper));

        Assert.Equal("Casa di <b>Anna</b>", Assert.Single(keeper.HeldRemovals).Name);

        // Held across runs: the name stays in the registry while the titles do.
        using (var again = SyncWith(MaterializerWith(keeper)))
        {
            await again.CleanUpAfterRemovalAsync(CancellationToken.None);
        }

        Assert.Equal("Casa di <b>Anna</b>", Assert.Single(keeper.HeldRemovals).Name);

        fails = false;
        using var sync = SyncWith(MaterializerWith(keeper));
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);

        Assert.Empty(_fixture.Registry.State.Stubs);
        Assert.Empty(keeper.HeldRemovals);
    }

    [Fact]
    public async Task ARemovalHeldForUsersThatCannotBeReadIsListedToo()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        Watch(_anna, FilmItem(a));
        _everyone = [];

        await RemoveAsync(a);

        Assert.Equal("A", Assert.Single(_keeper.HeldRemovals).Name);

        _everyone = [_anna, _bruno];
        using var sync = SyncWith(_materializer);
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);

        Assert.Empty(_keeper.HeldRemovals);
    }

    [Fact]
    public async Task AServerThatRemovesWithNoTroubleIsNeverListed()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        Watch(_anna, FilmItem(a));

        await RemoveAsync(a);

        Assert.Empty(_keeper.HeldRemovals);
    }

    // The old titles of the flat layout name their servers through their sources (row 412, migration from 0.4).
    [Fact]
    public async Task AFlatTitleHeldByTheMigrationListsTheServersOfItsSources()
    {
        var gone = Guid.NewGuid();
        var flat = new StubEntry
        {
            Id = "flat",
            Kind = StubKind.Movie,
            RelativePath = "Film/Sonda (2026)/Sonda (2026).strm",
            Sources = [new StubSource(gone, "i", "m")],
            IdentityKey = "k"
        };
        await _fixture.Registry.SaveAsync(
            StubState.Empty with { Stubs = [flat], RemovedServers = [gone], ServerNames = new Dictionary<Guid, string> { [gone] = "Vecchio" } },
            CancellationToken.None);
        var failing = new Mock<IKeptWatchStateStore>();
        failing.Setup(store => store.SaveAsync(It.IsAny<IReadOnlyCollection<KeptTitle>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("/secret"));
        using var keeper = Keeper(failing.Object);

        Assert.False(await keeper.KeepAsync(_fixture.Root, [flat], [], CancellationToken.None));

        Assert.Equal("Vecchio", Assert.Single(keeper.HeldRemovals).Name);
    }

    // A server removed by an older build has no stored name; the page then says so without one.
    [Fact]
    public async Task AHeldServerWithNoStoredNameIsListedWithoutOne()
    {
        var gone = Guid.NewGuid();
        var stub = new StubEntry
        {
            Id = "old",
            ServerId = gone,
            Kind = StubKind.Movie,
            RelativePath = gone.ToString("N") + "/Film/Sonda (2026)/Sonda (2026) - Vecchio.strm",
            Sources = [new StubSource(gone, "i", "m")],
            IdentityKey = "k"
        };
        await _fixture.Registry.SaveAsync(StubState.Empty with { Stubs = [stub], RemovedServers = [gone] }, CancellationToken.None);
        var failing = new Mock<IKeptWatchStateStore>();
        failing.Setup(store => store.SaveAsync(It.IsAny<IReadOnlyCollection<KeptTitle>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("/secret"));
        using var keeper = Keeper(failing.Object);

        Assert.False(await keeper.KeepAsync(_fixture.Root, [stub], [], CancellationToken.None));

        Assert.Null(Assert.Single(keeper.HeldRemovals).Name);
    }

    // Row 412 with decision 278: a failed save holds the automatic libraries too, since Jellyfin 12.4 drops the items of a
    // removed library and the next save would no longer find them.
    [Fact]
    public async Task AStoreThatFailsHoldsTheAutomaticLibrariesOfTheRemovedServer()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        Watch(_anna, FilmItem(a));
        var fails = true;
        var store = new Mock<IKeptWatchStateStore>();
        store.Setup(kept => kept.ReadMergesAsync(It.IsAny<CancellationToken>())).Returns<CancellationToken>(_kept.ReadMergesAsync);
        store.Setup(kept => kept.SaveAsync(It.IsAny<IReadOnlyCollection<KeptTitle>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyCollection<KeptTitle> titles, CancellationToken token) => fails ? throw new IOException("/secret") : _kept.SaveAsync(titles, token));
        using var keeper = Keeper(store.Object);
        var libraries = new ServerLibraryManagerTests.FakeLibraries();
        var fileSystem = new Mock<MediaBrowser.Model.IO.IFileSystem>();
        fileSystem.Setup(system => system.GetValidFilename(It.IsAny<string>())).Returns<string>(name => name);
        using var manager = new ServerLibraryManager(
            _fixture.Sync.Store,
            libraries.Object,
            fileSystem.Object,
            new ServerLibraryManagerTests.RecordingScans(),
            Mock.Of<ILibraryMaterializer>(),
            new ServerLibraryManagerTests.FakeRenamer { Libraries = libraries },
            _fixture.Registry,
            new LibraryPaths(libraries.Object, new CapturingLogger<LibraryPaths>()),
            Mock.Of<MediaBrowser.Controller.IServerApplicationHost>(),
            new CapturingLogger<ServerLibraryManager>());
        await manager.Enable(a.Id).Job;
        var created = libraries.Folders.Select(folder => folder.Name).ToList();

        await RemoveAsync(a, MaterializerWith(keeper));
        await manager.DeleteRemovedServerLibraries();

        Assert.Single(created);
        Assert.Equal(
            [FederatedPaths.Resolve(_fixture.Root, FederatedPaths.TypeFolderOf(a.Id, TestLibraries.Films, TypeFolder.Movies))],
            libraries.Created.Single().Options.PathInfos.Select(info => info.Path));
        Assert.Empty(libraries.Removed);
        Assert.Single(_fixture.Registry.State.Stubs);

        fails = false;
        using var sync = SyncWith(MaterializerWith(keeper));
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);
        await manager.DeleteRemovedServerLibraries();

        Assert.Empty(_fixture.Registry.State.Stubs);
        Assert.Single(await _kept.ReadAsync(CancellationToken.None));
        Assert.Equal(created.Order(StringComparer.Ordinal), libraries.Removed.Select(call => call.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheStubOfTheRemovedServerItselfGetsNothing()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        var old = FilmItem(a);
        await _kept.SaveAsync(
            [new KeptTitle { OldItemId = Guid.NewGuid(), Kind = KeptTitleKind.Movie, Title = "Sonda Senza ID", Year = 2026, RunTimeTicks = NinetyMinutes, SavedUtc = _fixture.Sync.Clock.Now.UtcDateTime, Users = [new KeptUserState(_anna.Id, true, 1, 0, _watched, false, null)] }],
            CancellationToken.None);
        using var sync = SyncWith(_materializer);
        await sync.RemoveServerAsync(a.Id, () => _fixture.Sync.Registry.RemoveAsync(a.Id), CancellationToken.None);

        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.False(Row(_anna, old).Played);
        Assert.Single(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TitlesNobodyWatchedAreNotKept()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        FilmItem(a);

        await RemoveAsync(a);

        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AServerThatNeverRemovedOneWritesNothing()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        await AddAndMaterializeAsync("A", TestLibraries.Films);
        var writes = _fixture.Database.Writes;

        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.Equal(writes, _fixture.Database.Writes);
    }

    [Fact]
    public async Task ASeriesWithoutIdsGetsItsEpisodesAndItsFavoriteBack()
    {
        _fixture.Sync.Client.Add(TestLibraries.Shows, RemoteItemType.Series, TestItems.Series("s", "Serie Senza ID", 2026) with { ProviderIds = new Dictionary<string, string?>() });
        _fixture.Sync.Client.Add(TestLibraries.Shows, RemoteItemType.Season, TestItems.Season("ss1", "s", 1));
        _fixture.Sync.Client.Add(TestLibraries.Shows, RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2, "Serie Senza ID") with { SeasonId = "ss1" });
        var a = await AddAndMaterializeAsync("A", TestLibraries.Shows);
        var (oldSeries, oldEpisode) = ShowItems(a);
        Row(_anna, oldSeries).IsFavorite = true;
        Watch(_anna, oldEpisode, 1);
        Resume(_bruno, oldEpisode, 7_000_000_000);

        await RemoveAsync(a);

        Assert.Empty(_fixture.Registry.State.Series);
        Assert.Equal(2, (await _kept.ReadAsync(CancellationToken.None)).Count);

        var again = await AddAndMaterializeAsync("A", TestLibraries.Shows);
        var (series, episode) = ShowItems(again);
        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.True(Row(_anna, series).IsFavorite);
        Assert.True(Row(_anna, episode).Played);
        Assert.Equal(7_000_000_000, Row(_bruno, episode).PlaybackPositionTicks);
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ASeriesWithTwoRowsOfOneIdGetsItsStateBack()
    {
        _fixture.Sync.Client.Add(TestLibraries.Shows, RemoteItemType.Series, TestItems.Series("s", "Serie Senza ID", 2026) with { ProviderIds = new Dictionary<string, string?>() });
        _fixture.Sync.Client.Add(TestLibraries.Shows, RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2, "Serie Senza ID"));
        var a = await AddAndMaterializeAsync("A", TestLibraries.Shows);
        var (_, oldEpisode) = ShowItems(a);
        Watch(_anna, oldEpisode, 1);
        await RemoveAsync(a);

        var again = await AddAndMaterializeAsync("A", TestLibraries.Shows);
        var (_, episode) = ShowItems(again);
        var state = _fixture.Registry.State;
        await _fixture.Registry.SaveAsync(state with { Series = [.. state.Series, state.Series[0] with { WorkKey = "other" }] }, CancellationToken.None);
        Assert.Equal(2, _fixture.Registry.State.Series.Count);
        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.True(Row(_anna, episode).Played);
    }

    // D21: a title merged by hand gets the kept state of each of its identities, on every copy of it.
    [Fact]
    public async Task AMergedTitleGetsTheKeptStateOfEachOfItsIdentities()
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.FilmsC, "C", "movies"));
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("x", "Alfa", 2026, "1") with { RunTimeTicks = NinetyMinutes });
        _fixture.Sync.Client.Add(TestLibraries.FilmsC, RemoteItemType.Movie, FilmWithoutIds("y"));
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        var c = await AddAndMaterializeAsync("C", TestLibraries.FilmsC);
        await MergeAsync((a, "x", "1"), (c, "y", null));
        var alfa = FilmItem(a);
        var sonda = FilmItem(c);
        await _kept.SaveAsync([KeptFilm("Alfa", "1", _anna), KeptFilm("Sonda Senza ID", null, _bruno)], CancellationToken.None);

        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.All(new[] { alfa, sonda }, film => Assert.True(Row(_anna, film).Played));
        Assert.All(new[] { alfa, sonda }, film => Assert.True(Row(_bruno, film).Played));
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    // D21: two films of one server merged by hand are one stub, which gets the kept state of each.
    [Fact]
    public async Task AStubOfTwoFilmsMergedByHandGetsTheKeptStateOfEach()
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.FilmsC, "C", "movies"));
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("x", "Alfa", 2026, "1") with { RunTimeTicks = NinetyMinutes });
        _fixture.Sync.Client.Add(TestLibraries.FilmsC, RemoteItemType.Movie, FilmWithoutIds("y"));
        var a = await _fixture.Sync.AddServerAsync("A", TestLibraries.Films, TestLibraries.FilmsC);
        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);
        await MergeAsync((a, "x", "1"), (a, "y", null));
        var film = FilmItem(a);
        await _kept.SaveAsync([KeptFilm("Alfa", "1", _anna), KeptFilm("Sonda Senza ID", null, _bruno)], CancellationToken.None);

        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.True(Row(_anna, film).Played);
        Assert.True(Row(_bruno, film).Played);
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    // D21 through a removal: the copy of the removed server finds the merged title by its own identity, and every copy of it.
    [Fact]
    public async Task TheCopyOfARemovedServerFindsEveryCopyOfAMergedTitle()
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.FilmsC, "C", "movies"));
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("x", "Alfa", 2026, "1") with { RunTimeTicks = NinetyMinutes });
        _fixture.Sync.Client.Add(TestLibraries.FilmsC, RemoteItemType.Movie, FilmWithoutIds("y"));
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        var b = await AddAndMaterializeAsync("B", TestLibraries.FilmsC);
        var c = await AddAndMaterializeAsync("C", TestLibraries.FilmsC);
        await MergeAsync((a, "x", "1"), (c, "y", null));
        var alfa = FilmItem(a);
        var sonda = FilmItem(c);
        Watch(_bruno, FilmItem(b));

        using (var sync = SyncWith(_materializer))
        {
            await sync.RemoveServerAsync(b.Id, () => _fixture.Sync.Registry.RemoveAsync(b.Id), CancellationToken.None);
            await _fixture.Merges.UpdateAsync(merges => ManualMergeService.WithoutServer(merges, b.Id.ToString("N")), CancellationToken.None);
            await sync.CleanUpAfterRemovalAsync(CancellationToken.None);
        }

        Assert.True(Row(_bruno, sonda).Played);
        Assert.True(Row(_bruno, alfa).Played);
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    // D33: the kept state belongs to the user on the title. A copy without IDs merged by hand sat on the removed server, so
    // the merge loses it, and the copy left on another server still finds the state.
    [Fact]
    public async Task TheKeptStateOfAMergedCopyOnTheRemovedServerFindsTheCopyLeft()
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.FilmsC, "C", "movies"));
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("x", "Alfa", 2026, "1") with { RunTimeTicks = NinetyMinutes });
        _fixture.Sync.Client.Add(TestLibraries.FilmsC, RemoteItemType.Movie, FilmWithoutIds("y"));
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        var c = await AddAndMaterializeAsync("C", TestLibraries.FilmsC);
        await MergeAsync((a, "x", "1"), (c, "y", null));
        var alfa = FilmItem(a);
        Watch(_bruno, FilmItem(c));

        using (var sync = SyncWith(_materializer))
        {
            await sync.RemoveServerAsync(c.Id, () => _fixture.Sync.Registry.RemoveAsync(c.Id), CancellationToken.None);
            // As the settings page does: the merges are kept for the watch state before the copies of the server leave them.
            Assert.True(await _keeper.KeepMergesAsync(c.Id, _fixture.Merges.Merges, CancellationToken.None));
            await _fixture.Merges.UpdateAsync(merges => ManualMergeService.WithoutServer(merges, c.Id.ToString("N")), CancellationToken.None);
            await sync.CleanUpAfterRemovalAsync(CancellationToken.None);
        }

        Assert.True(Row(_bruno, alfa).Played);
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    // The merges kept for a removed server serve only its own titles: one undone meanwhile joins nothing for another server.
    [Fact]
    public async Task TheMergesKeptForARemovedServerServeOnlyItsTitles()
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.FilmsB, "B", "movies"));
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.FilmsC, "C", "movies"));
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("x", "Alfa", 2026, "1") with { RunTimeTicks = NinetyMinutes });
        _fixture.Sync.Client.Add(TestLibraries.FilmsB, RemoteItemType.Movie, TestItems.Movie("z", "Zeta", 2024) with { ProviderIds = new Dictionary<string, string?>(), RunTimeTicks = NinetyMinutes });
        _fixture.Sync.Client.Add(TestLibraries.FilmsC, RemoteItemType.Movie, FilmWithoutIds("y"));
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        var b = await AddAndMaterializeAsync("B", TestLibraries.FilmsB);
        var c = await AddAndMaterializeAsync("C", TestLibraries.FilmsC);
        await MergeAsync((a, "x", "1"), (b, "z", null), (c, "y", null));
        var alfa = FilmItem(a);
        await RemoveAsKeptAsync(c);
        await _fixture.Merges.UpdateAsync(_ => [], CancellationToken.None);
        await _materializer.MaterializeAsync(CancellationToken.None);
        Watch(_anna, FilmItem(b));

        await RemoveAsKeptAsync(b);

        Assert.False(Row(_anna, alfa).Played);
        Assert.Empty(Assert.Single(await _kept.ReadAsync(CancellationToken.None)).Copies);
    }

    // The manual merges that cannot be read hold the titles of a removed server, which would miss their other copies.
    [Fact]
    public async Task WhileTheMergesCannotBeReadTheTitlesOfARemovedServerAreHeld()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        Watch(_anna, FilmItem(a));
        var damaged = new Mock<IManualMergeStore>();
        damaged.Setup(merges => merges.IsDamaged).Returns(true);
        damaged.Setup(merges => merges.Merges).Returns([]);
        using var keeper = Keeper(_kept, damaged.Object);

        await RemoveAsync(a, MaterializerWith(keeper));

        Assert.Single(_fixture.Registry.State.Stubs);
        Assert.Single(keeper.HeldRemovals);
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    // D33 for a series: its episodes and its favorite find the series left in the merge through its series items.
    [Fact]
    public async Task TheKeptStateOfAMergedSeriesOnTheRemovedServerFindsTheSeriesLeft()
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.ShowsC, "C", "tvshows"));
        _fixture.Sync.Client.Add(TestLibraries.Shows, RemoteItemType.Series, TestItems.Series("s", "Serie Senza ID", 2026) with { ProviderIds = new Dictionary<string, string?>() });
        _fixture.Sync.Client.Add(TestLibraries.Shows, RemoteItemType.Season, TestItems.Season("ss1", "s", 1));
        _fixture.Sync.Client.Add(TestLibraries.Shows, RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2, "Serie Senza ID") with { SeasonId = "ss1" });
        _fixture.Sync.Client.Add(TestLibraries.ShowsC, RemoteItemType.Series, TestItems.Series("t", "Altra Serie", 2025) with { ProviderIds = new Dictionary<string, string?>() });
        _fixture.Sync.Client.Add(TestLibraries.ShowsC, RemoteItemType.Season, TestItems.Season("ts1", "t", 1));
        _fixture.Sync.Client.Add(TestLibraries.ShowsC, RemoteItemType.Episode, TestItems.Episode("f", "t", 1, 2, "Altra Serie") with { SeasonId = "ts1" });
        var a = await AddAndMaterializeAsync("A", TestLibraries.Shows);
        var c = await AddAndMaterializeAsync("C", TestLibraries.ShowsC);
        var merge = new ManualMerge
        {
            Id = Guid.NewGuid(),
            CreatedUtc = SyncFixture.Start,
            Copies =
            [
                new MergedCopy { ServerId = a.Id.ToString("N"), ItemId = "s", Kind = ContentKind.Episode },
                new MergedCopy { ServerId = c.Id.ToString("N"), ItemId = "t", Kind = ContentKind.Episode }
            ]
        };
        await _fixture.Merges.UpdateAsync(_ => [merge], CancellationToken.None);
        await _materializer.MaterializeAsync(CancellationToken.None);
        var (series, episode) = ShowItems(a);
        var (oldSeries, oldEpisode) = ShowItems(c, "Altra Serie", 2025);
        Watch(_bruno, oldEpisode);
        Row(_bruno, oldSeries).IsFavorite = true;

        using (var sync = SyncWith(_materializer))
        {
            await sync.RemoveServerAsync(c.Id, () => _fixture.Sync.Registry.RemoveAsync(c.Id), CancellationToken.None);
            Assert.True(await _keeper.KeepMergesAsync(c.Id, _fixture.Merges.Merges, CancellationToken.None));
            await _fixture.Merges.UpdateAsync(merges => ManualMergeService.WithoutServer(merges, c.Id.ToString("N")), CancellationToken.None);
            await sync.CleanUpAfterRemovalAsync(CancellationToken.None);
        }

        Assert.True(Row(_bruno, episode).Played);
        Assert.True(Row(_bruno, series).IsFavorite);
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    // A suspended database holds the records without an error on every run.
    [Fact]
    public async Task WhileTheDatabaseIsSuspendedTheRecordsWaitWithoutAnError()
    {
        await _kept.SaveAsync([KeptFilm("Alfa", "1", _anna)], CancellationToken.None);
        _fixture.Database.Suspended = true;

        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.DoesNotContain(LogLevel.Error, _logger.Levels);
        _fixture.Database.Suspended = false;
        Assert.Single(await _kept.ReadAsync(CancellationToken.None));
    }

    // Merges that cannot be read could leave out a copy: the records wait for a run that reads them.
    [Fact]
    public async Task WhileTheMergesCannotBeReadTheRecordsWait()
    {
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("x", "Alfa", 2026, "1") with { RunTimeTicks = NinetyMinutes });
        var a = await AddAndMaterializeAsync("A", TestLibraries.Films);
        var alfa = FilmItem(a);
        await _kept.SaveAsync([KeptFilm("Alfa", "1", _anna)], CancellationToken.None);
        var damaged = new Mock<IManualMergeStore>();
        damaged.Setup(merges => merges.IsDamaged).Returns(true);
        damaged.Setup(merges => merges.Merges).Returns([]);
        using var keeper = Keeper(_kept, damaged.Object);

        await keeper.RestoreAsync(CancellationToken.None);

        Assert.False(Row(_anna, alfa).Played);
        Assert.Single(await _kept.ReadAsync(CancellationToken.None));
    }

    // Removes a server as the settings page does: its merges are kept for the watch state before its copies leave them.
    private async Task RemoveAsKeptAsync(RemoteServerInfo server)
    {
        var id = server.Id.ToString("N");
        using var sync = SyncWith(_materializer);
        await sync.RemoveServerAsync(server.Id, () => _fixture.Sync.Registry.RemoveAsync(server.Id), CancellationToken.None);
        Assert.True(await _keeper.KeepMergesAsync(server.Id, _fixture.Merges.Merges.Where(merge => ManualMergeService.HasCopyOn(merge, id)).ToList(), CancellationToken.None));
        await _fixture.Merges.UpdateAsync(merges => ManualMergeService.WithoutServer(merges, id), CancellationToken.None);
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);
    }

    private async Task MergeAsync(params (RemoteServerInfo Server, string ItemId, string? Tmdb)[] copies)
    {
        var merge = new ManualMerge
        {
            Id = Guid.NewGuid(),
            CreatedUtc = SyncFixture.Start,
            Copies = copies.Select(copy => new MergedCopy { ServerId = copy.Server.Id.ToString("N"), ItemId = copy.ItemId, Kind = ContentKind.Movie, Tmdb = copy.Tmdb }).ToList()
        };
        await _fixture.Merges.UpdateAsync(_ => [merge], CancellationToken.None);
        await _materializer.MaterializeAsync(CancellationToken.None);
    }

    private static KeptTitle KeptFilm(string title, string? tmdb, User user) => new()
    {
        OldItemId = Guid.NewGuid(),
        Kind = KeptTitleKind.Movie,
        Title = title,
        Year = 2026,
        Ids = new ContentIds(tmdb, null, null),
        RunTimeTicks = NinetyMinutes,
        SavedUtc = _watched,
        Users = [new KeptUserState(user.Id, true, 1, 0, _watched, false, null)]
    };

    // The items a scan makes of the series stub and its one episode.
    private (Series Series, Episode Episode) ShowItems(RemoteServerInfo server, string name = "Serie Senza ID", int year = 2026)
    {
        var entry = _fixture.Registry.State.Series.Single(series => series.ServerId == server.Id);
        var series = new Series { Id = Guid.NewGuid(), Path = FederatedPaths.Resolve(_fixture.Root, entry.RelativePath), Name = name, ProductionYear = year };
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            Path = FederatedPaths.Resolve(_fixture.Root, _fixture.Registry.State.Stubs.Single(stub => stub.ServerId == server.Id && stub.Kind != StubKind.Movie).RelativePath),
            Name = "Episode",
            SeriesId = series.Id,
            SeriesName = name,
            ParentIndexNumber = 1,
            IndexNumber = 2
        };
        Add(series);
        Add(episode);
        return (series, episode);
    }
}
