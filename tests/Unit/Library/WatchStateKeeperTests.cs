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
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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

        _kept = new KeptWatchStateStore(_data, new CapturingLogger<KeptWatchStateStore>());
        _keeper = Keeper(_kept);
        _materializer = MaterializerWith(_keeper);
    }

    public void Dispose()
    {
        _keeper.Dispose();
        _kept.Dispose();
        _fixture.Dispose();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, true);
        }
    }

    private static RemoteItem FilmWithoutIds(string id = "nf", long runtime = NinetyMinutes) =>
        TestItems.Movie(id, "Sonda Senza ID", 2026) with { ProviderIds = new Dictionary<string, string?>(), RunTimeTicks = runtime };

    private WatchStateKeeper Keeper(IKeptWatchStateStore store) => new(
        _fixture.Registry,
        _fixture.Sync.Index,
        _fixture.Sync.Registry,
        _fixture.Sync.Store,
        _fixture.Library.Object,
        _users.Object,
        _userData.Object,
        store,
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
        await sync.RemoveServerAsync(server.Id, () => Task.FromResult(_fixture.Sync.Registry.Remove(server.Id)), CancellationToken.None);
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);
    }

    private StubEntry StubOf(RemoteServerInfo server) => _fixture.Registry.State.Stubs.Single(stub => stub.ServerId == server.Id);

    // The library item a scan makes of a film stub, with the metadata of its .nfo.
    private Movie FilmItem(RemoteServerInfo server, long runtime = NinetyMinutes)
    {
        var item = new Movie
        {
            Id = Guid.NewGuid(),
            Path = FederatedPaths.Resolve(_fixture.Root, StubOf(server).RelativePath),
            Name = "Sonda Senza ID",
            ProductionYear = 2026,
            RunTimeTicks = runtime
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
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", "films");
        var old = FilmItem(a);
        Watch(_anna, old);
        Resume(_bruno, old, 9_000_000_000);

        await RemoveAsync(a);

        Assert.Empty(_fixture.Registry.State.Stubs);
        var kept = Assert.Single(await _kept.ReadAsync(CancellationToken.None));
        Assert.Equal(old.Id, kept.OldItemId);
        Assert.Equal(2, kept.Users.Count);

        // Added again it is a new server (row 408), and its stub has no item until the scan.
        var again = await AddAndMaterializeAsync("A", "films");
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
    public async Task TheStateGoesRightAwayToAServerThatAlreadyHasTheContent()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", "films");
        var b = await AddAndMaterializeAsync("B", "films");
        var old = FilmItem(a);
        var other = FilmItem(b);
        Watch(_anna, old);

        await RemoveAsync(a);

        Assert.True(Row(_anna, other).Played);
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AFilmWithoutIdsWhoseMetadataDiffersGetsNothing()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", "films");
        Watch(_anna, FilmItem(a));
        await RemoveAsync(a);
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)].Clear();
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithoutIds("other", NinetyMinutes * 2));

        var b = await AddAndMaterializeAsync("B", "films");
        var other = FilmItem(b, NinetyMinutes * 2);
        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.False(Row(_anna, other).Played);
        Assert.Single(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ANewerStateOnTheNewCopyWinsAndAFavoriteIsAdded()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", "films");
        var old = FilmItem(a);
        Watch(_anna, old);
        Row(_anna, old).IsFavorite = true;
        await RemoveAsync(a);
        var b = await AddAndMaterializeAsync("B", "films");
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
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", "films");
        var old = FilmItem(a);
        Watch(_anna, old);
        Watch(_bruno, old);
        await RemoveAsync(a);
        var b = await AddAndMaterializeAsync("B", "films");
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
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", "films");
        Watch(_anna, FilmItem(a));
        await RemoveAsync(a);
        var b = await AddAndMaterializeAsync("B", "films");
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
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", "films");
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
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", "films");
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
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", "films");
        var old = FilmItem(a);
        Watch(_anna, old);
        var path = old.Path;
        _everyone = [];

        await RemoveAsync(a);

        Assert.True(File.Exists(path));
        Assert.Single(_fixture.Registry.State.Stubs);
        Assert.False(File.Exists(Path.Combine(_data, KeptWatchStateStore.FileName)));

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
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", "films");
        Watch(_anna, FilmItem(a));
        var failing = new Mock<IKeptWatchStateStore>();
        failing.Setup(store => store.SaveAsync(It.IsAny<IReadOnlyCollection<KeptTitle>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("/secret"));
        using var keeper = Keeper(failing.Object);

        await RemoveAsync(a, MaterializerWith(keeper));

        Assert.Single(_fixture.Registry.State.Stubs);
        Assert.True(Directory.Exists(_fixture.Path_(a)));
        Assert.DoesNotContain(_logger.Lines, message => message.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheStubOfTheRemovedServerItselfGetsNothing()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", "films");
        var old = FilmItem(a);
        await _kept.SaveAsync(
            [new KeptTitle { OldItemId = Guid.NewGuid(), Kind = KeptTitleKind.Movie, Title = "Sonda Senza ID", Year = 2026, RunTimeTicks = NinetyMinutes, SavedUtc = _fixture.Sync.Clock.Now.UtcDateTime, Users = [new KeptUserState(_anna.Id, true, 1, 0, _watched, false, null)] }],
            CancellationToken.None);
        using var sync = SyncWith(_materializer);
        await sync.RemoveServerAsync(a.Id, () => Task.FromResult(_fixture.Sync.Registry.Remove(a.Id)), CancellationToken.None);

        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.False(Row(_anna, old).Played);
        Assert.Single(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TitlesNobodyWatchedAreNotKept()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithoutIds());
        var a = await AddAndMaterializeAsync("A", "films");
        FilmItem(a);

        await RemoveAsync(a);

        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AServerThatNeverRemovedOneMakesNoFile()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithoutIds());
        await AddAndMaterializeAsync("A", "films");

        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_data, KeptWatchStateStore.FileName)));
    }

    [Fact]
    public async Task ASeriesWithoutIdsGetsItsEpisodesAndItsFavoriteBack()
    {
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s", "Serie Senza ID", 2026) with { ProviderIds = new Dictionary<string, string?>() });
        _fixture.Sync.Client.Add("shows", RemoteItemType.Season, TestItems.Season("ss1", "s", 1));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2, "Serie Senza ID") with { SeasonId = "ss1" });
        var a = await AddAndMaterializeAsync("A", "shows");
        var (oldSeries, oldEpisode) = ShowItems(a);
        Row(_anna, oldSeries).IsFavorite = true;
        Watch(_anna, oldEpisode, 1);
        Resume(_bruno, oldEpisode, 7_000_000_000);

        await RemoveAsync(a);

        Assert.Empty(_fixture.Registry.State.Series);
        Assert.Equal(2, (await _kept.ReadAsync(CancellationToken.None)).Count);

        var again = await AddAndMaterializeAsync("A", "shows");
        var (series, episode) = ShowItems(again);
        await _keeper.RestoreAsync(CancellationToken.None);

        Assert.True(Row(_anna, series).IsFavorite);
        Assert.True(Row(_anna, episode).Played);
        Assert.Equal(7_000_000_000, Row(_bruno, episode).PlaybackPositionTicks);
        Assert.Empty(await _kept.ReadAsync(CancellationToken.None));
    }

    // The items a scan makes of the series stub and its one episode.
    private (Series Series, Episode Episode) ShowItems(RemoteServerInfo server)
    {
        var entry = _fixture.Registry.State.Series.Single(series => series.ServerId == server.Id);
        var series = new Series { Id = Guid.NewGuid(), Path = FederatedPaths.Resolve(_fixture.Root, entry.RelativePath), Name = "Serie Senza ID", ProductionYear = 2026 };
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            Path = FederatedPaths.Resolve(_fixture.Root, StubOf(server).RelativePath),
            Name = "Episode",
            SeriesId = series.Id,
            SeriesName = "Serie Senza ID",
            ParentIndexNumber = 1,
            IndexNumber = 2
        };
        Add(series);
        Add(episode);
        return (series, episode);
    }
}
