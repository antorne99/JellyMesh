using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Tests.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;
using LinkedChildType = MediaBrowser.Controller.Entities.LinkedChildType;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The manual migration from the flat layout: the state goes to the new versions once the admin links the new paths, then
// the old folders are emptied (Q40, Q41, Q47-Q49, D12, D15).
public sealed class LayoutMigrationTests : IDisposable
{
    private static readonly DateTime _watched = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid _serverA = Guid.NewGuid();
    private static readonly Guid _serverB = Guid.NewGuid();

    private readonly string _data = Path.Combine(Path.GetTempPath(), "jellymesh-migration-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryConfigurationStore _store = new();
    private readonly WatchStateCarryLog _log;
    private readonly InMemoryLastServedStore _lastServed = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Mock<IRemoteServerRegistry> _servers = new();
    private readonly Mock<IFederationIndex> _index = new();
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly Dictionary<(Guid User, Guid Item), UserItemData> _rows = [];
    private readonly HashSet<(Guid User, Guid Item)> _hidden = [];
    private readonly List<StubEntry> _stubs = [];
    private readonly List<SeriesStub> _series = [];
    private readonly List<VersionGroup> _groups = [];
    private readonly List<VirtualFolderInfo> _libraries = [];
    private readonly List<(Guid Server, string ItemId)> _indexed = [];
    private readonly User _mario = new("mario", "auth", "reset");
    private readonly User _luigi = new("luigi", "auth", "reset");
    private StubRegistry _registry;

    public LayoutMigrationTests()
    {
        _store.Configuration.FederatedFolderPath = Path.Combine(_data, "federata");
        _registry = NewRegistry();
        _log = new WatchStateCarryLog(Path.Combine(_data, "log"), new CapturingLogger<WatchStateCarryLog>());

        _library.Setup(library => library.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
        _library.Setup(library => library.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User>()))
            .Returns((Guid id, User user) => _hidden.Contains((user.Id, id)) ? null : _items.GetValueOrDefault(id));
        _library.Setup(library => library.FindByPath(It.IsAny<string>(), It.IsAny<bool?>()))
            .Returns((string path, bool? _) => _items.Values.FirstOrDefault(item => item.Path == path));
        _library.Setup(library => library.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()))
            .Callback((BaseItem item, DeleteOptions _) => _items.Remove(item.Id));
        _library.Setup(library => library.GetVirtualFolders()).Returns(() => _libraries.ToList());
        _users.Setup(users => users.GetUsers()).Returns(() => [_mario, _luigi]);
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
            .Callback((User user, BaseItem item, UserItemData data, UserDataSaveReason _, CancellationToken _) => _rows[(user.Id, item.Id)] = data);
        _servers.Setup(servers => servers.GetServers()).Returns(() =>
        [
            new RemoteServerInfo(_serverA, "Casa", "http://a", RemoteMode.Standard, 0, [], null, null),
            new RemoteServerInfo(_serverB, "Ufficio", "http://b", RemoteMode.Standard, 0, [], null, null)
        ]);
        _index.Setup(index => index.Snapshot).Returns(() => new IndexSnapshot
        {
            Version = IndexSnapshot.CurrentVersion,
            Health = IndexHealth.Ok,
            Servers = new[] { _serverA, _serverB }.Select(server => new ServerCatalog
            {
                ServerId = server,
                State = ServerSyncState.Complete,
                Items = _indexed.Where(entry => entry.Server == server).Select(entry => Indexed(entry.ItemId)).ToList()
            }).ToList()
        });
    }

    private string Root => _store.Configuration.FederatedFolderPath;

    public void Dispose()
    {
        _registry.Dispose();
        _log.Dispose();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, true);
        }
    }

    [Fact]
    public async Task OnceTheNewPathsAreLinkedTheStateGoesToTheNewVersionsAndTheOldFolderIsEmptied()
    {
        var (old, oldItem) = OldFilm("Dune", (_serverA, "a1"), (_serverB, "b1"));
        var a = NewFilm(_serverA, "a1");
        var b = NewFilm(_serverB, "b1");
        Link(b, a);
        AllPathsLinked();
        var row = Row(_mario, oldItem);
        row.PlaybackPositionTicks = 500;
        row.LastPlayedDate = _watched;
        row.PlayCount = 1;
        row.IsFavorite = true;
        await _lastServed.RecordAsync(_mario.Id, old.Id, _serverA, CancellationToken.None);

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.Equal(500, Row(_mario, a).PlaybackPositionTicks);
        Assert.Equal(0, Row(_mario, b).PlaybackPositionTicks);
        Assert.True(Row(_mario, b).IsFavorite);
        Assert.DoesNotContain(_registry.State.Stubs, stub => stub.ServerId is null);
        Assert.False(_items.ContainsKey(oldItem.Id));
        Assert.Equal(2, _registry.State.Stubs.Count);
    }

    [Fact]
    public async Task WithoutTheNewPathsNothingIsCarriedAndTheOldStubsStay()
    {
        var (_, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var a = NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));
        Row(_mario, oldItem).IsFavorite = true;

        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);

        Assert.False(Row(_mario, a).IsFavorite);
        Assert.Single(_registry.State.Stubs, stub => stub.ServerId is null);
        Assert.True(_items.ContainsKey(oldItem.Id));
        var status = migration.GetStatus(Root)!;
        Assert.Equal(LayoutMigrationStep.AddPaths, status.Step);
        var library = Assert.Single(status.Libraries);
        Assert.Equal("Film", library.Name);
        Assert.Equal([OldFilms], library.OldPaths);
        Assert.Equal([FederatedFolderLayout.ServerMoviesPath(Root, _serverA), FederatedFolderLayout.ServerMoviesPath(Root, _serverB)], library.MissingPaths);
        Assert.Equal(1, status.OldTitles);
    }

    [Fact]
    public async Task ALibraryOnTheOldSeriesPathNeedsOnlyTheSeriesPathsOfEachServer()
    {
        OldFilm("Dune", (_serverA, "a1"));
        _libraries.Add(Library("Serie", CollectionTypeOptions.tvshows, OldSeries, FederatedFolderLayout.ServerSeriesPath(Root, _serverA)));

        var status = (await MigrationAsync()).GetStatus(Root)!;

        var library = Assert.Single(status.Libraries);
        Assert.Equal([OldSeries], library.OldPaths);
        Assert.Equal([FederatedFolderLayout.ServerSeriesPath(Root, _serverB)], library.MissingPaths);
    }

    [Fact]
    public async Task AFreshInstallShowsNoMigration()
    {
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, FederatedFolderLayout.ServerMoviesPath(Root, _serverA)));

        Assert.Null((await MigrationAsync()).GetStatus(Root));
    }

    [Fact]
    public async Task AfterTheEmptyingThePageAsksToRemoveTheOldPaths()
    {
        var (_, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var a = NewFilm(_serverA, "a1");
        AllPathsLinked();
        Row(_mario, oldItem).IsFavorite = true;

        var migration = await MigrationAsync();
        Assert.Equal(LayoutMigrationStep.Carrying, migration.GetStatus(Root)!.Step);
        await migration.RunAsync(CancellationToken.None);

        Assert.True(Row(_mario, a).IsFavorite);
        var status = migration.GetStatus(Root)!;
        Assert.Equal(LayoutMigrationStep.RemoveOldPaths, status.Step);
        Assert.Equal([OldFilms, OldSeries], Assert.Single(status.Libraries).OldPaths);
        Assert.Equal(0, status.OldTitles);

        _libraries[0] = Library("Film", CollectionTypeOptions.mixed, NewPaths().ToArray());
        Assert.Null(migration.GetStatus(Root));
    }

    [Fact]
    public async Task ATitleNotLinkedYetKeepsEveryOldStubUntilALaterRunAfterARestart()
    {
        var (_, waitingItem) = OldFilm("Dune", (_serverA, "a1"), (_serverB, "b1"));
        var (_, readyItem) = OldFilm("Alien", (_serverA, "a2"));
        var a = NewFilm(_serverA, "a1");
        var b = NewFilm(_serverB, "b1");
        var ready = NewFilm(_serverA, "a2");
        Group(a, b);
        AllPathsLinked();
        Row(_mario, waitingItem).IsFavorite = true;
        Row(_mario, readyItem).IsFavorite = true;

        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);

        Assert.True(Row(_mario, ready).IsFavorite);
        Assert.False(Row(_mario, a).IsFavorite || Row(_mario, b).IsFavorite);
        Assert.Equal(2, _registry.State.Stubs.Count(stub => stub.ServerId is null));
        Assert.Equal(1, migration.GetStatus(Root)!.Waiting);

        // A restart: a new registry and migration over the same files.
        _registry.Dispose();
        _registry = NewRegistry();
        Link(b, a);
        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.True(Row(_mario, b).IsFavorite);
        Assert.DoesNotContain(_registry.State.Stubs, stub => stub.ServerId is null);
        Assert.False(_items.ContainsKey(waitingItem.Id) || _items.ContainsKey(readyItem.Id));
    }

    [Fact]
    public async Task ANewVersionNotScannedYetKeepsTheOldStubs()
    {
        var (_, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var a = NewFilm(_serverA, "a1");
        _items.Remove(a.Id);
        AllPathsLinked();
        Row(_mario, oldItem).IsFavorite = true;

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.Single(_registry.State.Stubs, stub => stub.ServerId is null);
        Assert.True(_items.ContainsKey(oldItem.Id));
    }

    [Fact]
    public async Task AUserWhoSeesNoNewVersionDoesNotHoldTheEmptying()
    {
        var (_, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var a = NewFilm(_serverA, "a1");
        AllPathsLinked();
        Row(_mario, oldItem).IsFavorite = true;
        Row(_luigi, oldItem).IsFavorite = true;
        _hidden.Add((_luigi.Id, a.Id));

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.True(Row(_mario, a).IsFavorite);
        Assert.DoesNotContain(_registry.State.Stubs, stub => stub.ServerId is null);
    }

    [Fact]
    public async Task ATitleGoneFromEveryServerDoesNotHoldTheEmptyingButOneStillThereDoes()
    {
        var (gone, _) = OldFilm("Gone", (_serverA, "g1"));
        _indexed.Remove((_serverA, "g1"));
        AllPathsLinked();

        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);
        Assert.Null(_registry.Find(gone.Id));

        _stubs.Remove(gone);
        OldFilm("Kept", (_serverB, "k1"));
        await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);
        Assert.Single(_registry.State.Stubs, stub => stub.ServerId is null);
    }

    [Fact]
    public async Task AnOldSeriesGivesItsFavoriteToTheNewSeriesAndGoesWithItsEpisodes()
    {
        var (oldShow, oldShowItem) = OldShow("Lost", (_serverA, "s1"));
        var (_, oldEpisodeItem) = OldEpisode(oldShow, (_serverA, "e1"));
        var (newShow, newShowItem) = NewShow(_serverA, "s1");
        var episode = NewEpisode(newShow, _serverA, "e1");
        AllPathsLinked();
        Row(_mario, oldShowItem).IsFavorite = true;
        Row(_mario, oldEpisodeItem).Played = true;
        Row(_mario, oldEpisodeItem).LastPlayedDate = _watched;

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.True(Row(_mario, newShowItem).IsFavorite);
        Assert.True(Row(_mario, episode).Played);
        Assert.DoesNotContain(_registry.State.Series, series => series.ServerId is null);
        Assert.DoesNotContain(_registry.State.Stubs, stub => stub.ServerId is null);
        Assert.False(_items.ContainsKey(oldShowItem.Id));
    }

    [Fact]
    public async Task AnOldSeriesLeftWithoutEpisodesGoesToo()
    {
        var (oldShow, oldShowItem) = OldShow("Lost", (_serverA, "s1"));
        var (_, newShowItem) = NewShow(_serverA, "s1");
        AllPathsLinked();
        Row(_mario, oldShowItem).IsFavorite = true;

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.True(Row(_mario, newShowItem).IsFavorite);
        Assert.DoesNotContain(_registry.State.Series, series => series.Id == oldShow.Id);
        Assert.False(_items.ContainsKey(oldShowItem.Id));
    }

    [Fact]
    public async Task ALocalPrimaryTakesTheFavoriteOfTheOldStub()
    {
        var (_, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var a = NewFilm(_serverA, "a1");
        var local = Add(new Movie { Path = "/media/film/Dune.mkv" });
        Link(local, a);
        AllPathsLinked();
        Row(_mario, oldItem).IsFavorite = true;

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.True(Row(_mario, local).IsFavorite);
        Assert.DoesNotContain(_registry.State.Stubs, stub => stub.ServerId is null);
    }

    [Fact]
    public async Task AGroupWithALocalCopyNotLinkedYetKeepsTheOldStub()
    {
        var (_, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var a = NewFilm(_serverA, "a1");
        var local = Add(new Movie { Path = "/media/film/Dune.mkv" });
        Group(local, a);
        AllPathsLinked();
        Row(_mario, oldItem).IsFavorite = true;

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.False(Row(_mario, a).IsFavorite);
        Assert.Single(_registry.State.Stubs, stub => stub.ServerId is null);
    }

    private static string Id() => Guid.NewGuid().ToString("N");

    private static IndexedItem Indexed(string itemId) => new() { ItemId = itemId, Kind = IndexedItemKind.Movie, LibraryId = "lib", LastSeenUtc = DateTimeOffset.UnixEpoch };

    private static VirtualFolderInfo Library(string name, CollectionTypeOptions type, params string[] paths) =>
        new() { Name = name, CollectionType = type, Locations = paths };

    private static StubSource Source((Guid Server, string ItemId) source) => new(source.Server, source.ItemId, source.ItemId);

    private string OldFilms => FederatedFolderLayout.MoviesPath(Root);

    private string OldSeries => FederatedFolderLayout.SeriesPath(Root);

    private StubRegistry NewRegistry() => new(Path.Combine(_data, "registry"), _store, new CapturingLogger<StubRegistry>());

    private IEnumerable<string> NewPaths() => new[] { _serverA, _serverB }
        .SelectMany(server => new[] { FederatedFolderLayout.ServerMoviesPath(Root, server), FederatedFolderLayout.ServerSeriesPath(Root, server) });

    // One mixed library with the old paths and every new one.
    private void AllPathsLinked() => _libraries.Add(Library("Film", CollectionTypeOptions.mixed, NewPaths().Prepend(OldSeries).Prepend(OldFilms).ToArray()));

    private (StubEntry Stub, Movie Item) OldFilm(string title, params (Guid Server, string ItemId)[] sources)
    {
        var stub = new StubEntry
        {
            Id = Id(),
            Kind = StubKind.Movie,
            RelativePath = Path.Combine("Film", title, title + ".strm"),
            IdentityKey = "old:" + title,
            Sources = sources.Select(Source).ToList()
        };
        _stubs.Add(stub);
        _indexed.AddRange(sources);
        return (stub, Add(new Movie { Path = FederatedPaths.Resolve(Root, stub.RelativePath) }));
    }

    private Movie NewFilm(Guid server, string itemId)
    {
        var name = Id();
        var stub = new StubEntry
        {
            Id = name,
            ServerId = server,
            Kind = StubKind.Movie,
            RelativePath = Path.Combine(FederatedPaths.ServerFolder(server), "Film", name, name + ".strm"),
            IdentityKey = "new:" + itemId,
            Sources = [Source((server, itemId))]
        };
        _stubs.Add(stub);
        return Add(new Movie { Path = FederatedPaths.Resolve(Root, stub.RelativePath) });
    }

    private (SeriesStub Stub, Series Item) OldShow(string title, (Guid Server, string ItemId) source)
    {
        var stub = new SeriesStub { Id = Id(), RelativePath = Path.Combine("Serie", title), WorkKey = "work:" + title, Sources = [Source(source)] };
        _series.Add(stub);
        _indexed.Add(source);
        return (stub, Add(new Series { Path = FederatedPaths.Resolve(Root, stub.RelativePath) }));
    }

    private (SeriesStub Stub, Series Item) NewShow(Guid server, string itemId)
    {
        var name = Id();
        var stub = new SeriesStub
        {
            Id = name,
            ServerId = server,
            RelativePath = Path.Combine(FederatedPaths.ServerFolder(server), "Serie", name),
            WorkKey = "work:" + name,
            Sources = [Source((server, itemId))]
        };
        _series.Add(stub);
        return (stub, Add(new Series { Path = FederatedPaths.Resolve(Root, stub.RelativePath) }));
    }

    private (StubEntry Stub, Episode Item) OldEpisode(SeriesStub show, (Guid Server, string ItemId) source)
    {
        var stub = new StubEntry
        {
            Id = Id(),
            Kind = StubKind.Episode,
            RelativePath = Path.Combine(show.RelativePath, "Season 01", "S01E01.strm"),
            IdentityKey = "old:" + source.ItemId,
            SeriesStubId = show.Id,
            SeasonNumber = 1,
            EpisodeNumber = 1,
            Sources = [Source(source)]
        };
        _stubs.Add(stub);
        _indexed.Add(source);
        return (stub, Add(new Episode { Path = FederatedPaths.Resolve(Root, stub.RelativePath) }));
    }

    private Episode NewEpisode(SeriesStub show, Guid server, string itemId)
    {
        var stub = new StubEntry
        {
            Id = Id(),
            ServerId = server,
            Kind = StubKind.Episode,
            RelativePath = Path.Combine(show.RelativePath, "Season 01", "S01E01.strm"),
            IdentityKey = "new:" + itemId,
            SeriesStubId = show.Id,
            SeasonNumber = 1,
            EpisodeNumber = 1,
            Sources = [Source((server, itemId))]
        };
        _stubs.Add(stub);
        return Add(new Episode { Path = FederatedPaths.Resolve(Root, stub.RelativePath) });
    }

    private T Add<T>(T item)
        where T : BaseItem
    {
        item.Id = Guid.NewGuid();
        _items[item.Id] = item;
        return item;
    }

    // Puts the versions in one registry group, without linking them yet.
    private void Group(params Video[] members)
    {
        var stubIds = members.Select(member => _stubs.FirstOrDefault(stub => FederatedPaths.Resolve(Root, stub.RelativePath) == member.Path)?.Id).OfType<string>().ToList();
        _groups.RemoveAll(group => group.StubIds.Any(stubIds.Contains));
        _groups.Add(new VersionGroup
        {
            Id = Id(),
            IdentityKey = "key",
            StubIds = stubIds,
            LocalItemIds = members.Where(member => !member.Path.StartsWith(Root, StringComparison.Ordinal)).Select(member => member.Id).ToList()
        });
    }

    // Links the versions as the linker does and puts them in one registry group.
    private void Link(Video primary, params Video[] alternates)
    {
        foreach (var alternate in alternates)
        {
            alternate.SetPrimaryVersionId(primary.Id);
        }

        primary.LinkedAlternateVersions = alternates.Select(alternate => new LinkedChild { ItemId = alternate.Id, Type = LinkedChildType.LinkedAlternateVersion }).ToArray();
        Group(alternates.Prepend(primary).ToArray());
    }

    private async Task<LayoutMigration> MigrationAsync()
    {
        await _registry.SaveAsync(StubState.Empty with { Stubs = _stubs.ToList(), Series = _series.ToList(), Groups = _groups.ToList() }, CancellationToken.None);
        var carrier = new WatchStateCarrier(_registry, _store, _library.Object, _users.Object, _userData.Object, _lastServed, _log, new CapturingLogger<WatchStateCarrier>());
        return new LayoutMigration(_registry, _store, _library.Object, _servers.Object, _index.Object, carrier, new StubRemover(_library.Object), new CapturingLogger<LayoutMigration>());
    }

    private UserItemData Row(User user, BaseItem item) => _userData.Object.GetUserData(user, item)!;
}
