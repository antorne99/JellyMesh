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
using Microsoft.Extensions.Logging;
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
    private readonly Mock<IVersionLinker> _linker = new();
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly Dictionary<(Guid User, Guid Item), UserItemData> _rows = [];
    private readonly HashSet<(Guid User, Guid Item)> _hidden = [];
    private readonly List<StubEntry> _stubs = [];
    private readonly List<SeriesStub> _series = [];
    private readonly List<VersionGroup> _groups = [];
    private readonly List<VirtualFolderInfo> _libraries = [];
    private readonly List<(Guid Server, string ItemId)> _indexed = [];
    private readonly List<Guid> _configured = [_serverA, _serverB];
    private readonly List<Guid> _synced = [_serverA, _serverB];
    private readonly List<Guid> _removed = [];
    private readonly List<string> _imported = [];
    private readonly CapturingLogger<LayoutMigration> _logger = new();
    private Func<WatchStateMove, WatchStateMoveOutcome>? _outcome;
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
            _configured.Select(server => new RemoteServerInfo(server, "Server", "http://" + server.ToString("N"), RemoteMode.Standard, 0, _imported.ToList(), null, null)).ToList());
        _servers.Setup(servers => servers.GetLibrariesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RemoteResult.Success<IReadOnlyList<RemoteLibrary>>([]));
        _index.Setup(index => index.Snapshot).Returns(() => new IndexSnapshot
        {
            Version = IndexSnapshot.CurrentVersion,
            Health = IndexHealth.Ok,
            Servers = _synced.Select(server => new ServerCatalog
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
        Assert.Empty(status.UnlinkedFolders);
        Assert.Equal(1, status.OldTitles);
    }

    [Fact]
    public async Task OldPathsRemovedBeforeTheNewOnesAreAddedKeepTheOldStubs()
    {
        var (old, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var a = NewFilm(_serverA, "a1");
        _items.Remove(oldItem.Id);
        _items.Remove(a.Id);

        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);

        Assert.NotNull(_registry.Find(old.Id));
        var status = migration.GetStatus(Root)!;
        Assert.Equal(LayoutMigrationStep.AddPaths, status.Step);
        Assert.Empty(status.Libraries);
        Assert.Equal([FederatedFolderLayout.ServerMoviesPath(Root, _serverA), FederatedFolderLayout.ServerMoviesPath(Root, _serverB)], status.UnlinkedFolders);
        Assert.Equal(1, status.OldTitles);
    }

    [Fact]
    public async Task TheOldFoldersWaitForTheSeriesFoldersOfEachServerInSomeLibrary()
    {
        var (old, _) = OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        var (oldShow, _) = OldShow("Lost", (_serverA, "s1"));
        OldEpisode(oldShow, (_serverA, "e1"));
        var (newShow, _) = NewShow(_serverA, "s1");
        NewEpisode(newShow, _serverA, "e1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies,
            [OldFilms, FederatedFolderLayout.ServerMoviesPath(Root, _serverA), FederatedFolderLayout.ServerMoviesPath(Root, _serverB)]));

        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);

        Assert.NotNull(_registry.Find(old.Id));
        Assert.Equal(LayoutMigrationStep.AddPaths, migration.GetStatus(Root)!.Step);
        Assert.Equal([FederatedFolderLayout.ServerSeriesPath(Root, _serverA), FederatedFolderLayout.ServerSeriesPath(Root, _serverB)], migration.GetStatus(Root)!.UnlinkedFolders);

        _libraries.Add(Library("Serie", CollectionTypeOptions.tvshows,
            [FederatedFolderLayout.ServerSeriesPath(Root, _serverA), FederatedFolderLayout.ServerSeriesPath(Root, _serverB)]));
        await migration.RunAsync(CancellationToken.None);

        Assert.DoesNotContain(_registry.State.Stubs, stub => stub.ServerId is null);
        Assert.DoesNotContain(_registry.State.Series, series => series.ServerId is null);
    }

    [Fact]
    public async Task AnInstallWithOnlyFilmsFinishesWithoutTheSeriesFolders()
    {
        var (old, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var a = NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies,
            [OldFilms, FederatedFolderLayout.ServerMoviesPath(Root, _serverA), FederatedFolderLayout.ServerMoviesPath(Root, _serverB)]));
        _libraries.Add(Library("Serie", CollectionTypeOptions.tvshows, OldSeries));
        Row(_mario, oldItem).IsFavorite = true;

        var migration = await MigrationAsync();
        var before = migration.GetStatus(Root)!;
        await migration.RunAsync(CancellationToken.None);

        Assert.Equal(LayoutMigrationStep.Carrying, before.Step);
        Assert.Empty(before.UnlinkedFolders);
        Assert.All(before.Libraries, library => Assert.Empty(library.MissingPaths));
        Assert.True(Row(_mario, a).IsFavorite);
        Assert.Null(_registry.Find(old.Id));
        Assert.Equal(LayoutMigrationStep.RemoveOldPaths, migration.GetStatus(Root)!.Step);
    }

    [Fact]
    public async Task AnInstallWithOnlySeriesFinishesWithoutTheFilmFolders()
    {
        var (oldShow, oldShowItem) = OldShow("Lost", (_serverA, "s1"));
        OldEpisode(oldShow, (_serverA, "e1"));
        var (newShow, newShowItem) = NewShow(_serverA, "s1");
        NewEpisode(newShow, _serverA, "e1");
        _libraries.Add(Library("Serie", CollectionTypeOptions.tvshows,
            [OldSeries, FederatedFolderLayout.ServerSeriesPath(Root, _serverA), FederatedFolderLayout.ServerSeriesPath(Root, _serverB)]));
        Row(_mario, oldShowItem).IsFavorite = true;

        var migration = await MigrationAsync();
        Assert.Equal(LayoutMigrationStep.Carrying, migration.GetStatus(Root)!.Step);
        await migration.RunAsync(CancellationToken.None);

        Assert.True(Row(_mario, newShowItem).IsFavorite);
        Assert.DoesNotContain(_registry.State.Series, series => series.ServerId is null);
        Assert.DoesNotContain(_registry.State.Stubs, stub => stub.ServerId is null);
    }

    [Fact]
    public async Task AnOldEpisodeAloneNeedsTheSeriesFolders()
    {
        var (oldShow, _) = OldShow("Lost", (_serverA, "s1"));
        var (episode, _) = OldEpisode(oldShow, (_serverA, "e1"));
        _series.Remove(oldShow);
        _libraries.Add(Library("Film", CollectionTypeOptions.movies,
            [OldFilms, FederatedFolderLayout.ServerMoviesPath(Root, _serverA), FederatedFolderLayout.ServerMoviesPath(Root, _serverB)]));

        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);

        Assert.NotNull(_registry.Find(episode.Id));
        Assert.Equal([FederatedFolderLayout.ServerSeriesPath(Root, _serverA), FederatedFolderLayout.ServerSeriesPath(Root, _serverB)], migration.GetStatus(Root)!.UnlinkedFolders);
    }

    [Fact]
    public async Task OldPathsRemovedAfterTheNewOnesAreAddedLetAnOldStubWithoutItsItemGo()
    {
        var (old, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        _items.Remove(oldItem.Id);
        _libraries.Add(Library("Nuovi", CollectionTypeOptions.mixed, NewPaths().ToArray()));

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.Null(_registry.Find(old.Id));
    }

    [Fact]
    public async Task ALibraryOnTheOldSeriesPathNeedsOnlyTheSeriesPathsOfEachServer()
    {
        OldFilm("Dune", (_serverA, "a1"));
        OldShow("Lost", (_serverA, "s1"));
        _libraries.Add(Library("Serie", CollectionTypeOptions.tvshows, OldSeries, FederatedFolderLayout.ServerSeriesPath(Root, _serverA)));

        var status = (await MigrationAsync()).GetStatus(Root)!;

        var library = Assert.Single(status.Libraries);
        Assert.Equal([OldSeries], library.OldPaths);
        Assert.Equal([FederatedFolderLayout.ServerSeriesPath(Root, _serverB)], library.MissingPaths);
    }

    [Fact]
    public async Task WithNoServerConfiguredTheMigrationDoesNotStart()
    {
        var (old, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        _items.Remove(oldItem.Id);
        _configured.Clear();
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.NotNull(_registry.Find(old.Id));
    }

    [Fact]
    public async Task AServerListEmptiedBeforeTheLockKeepsTheOldStubs()
    {
        var (old, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        _items.Remove(oldItem.Id);
        AllPathsLinked();

        // A removal from the page empties the list once the gate has passed; the run reads the index inside the lock.
        var snapshot = _index.Object.Snapshot;
        _index.Setup(index => index.Snapshot).Returns(() =>
        {
            _configured.Clear();
            return snapshot;
        });

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.NotNull(_registry.Find(old.Id));
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
    public async Task AConfiguredServerMissingFromAnEmptyIndexKeepsTheOldStubsWaiting()
    {
        var (old, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        _synced.Clear();
        AllPathsLinked();

        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);

        Assert.NotNull(_registry.Find(old.Id));
        Assert.True(_items.ContainsKey(oldItem.Id));
        Assert.Equal(1, migration.GetStatus(Root)!.Waiting);
    }

    [Fact]
    public async Task AConfiguredServerNotReadInFullKeepsTheOldStubsWaiting()
    {
        var (old, _) = OldFilm("Dune", (_serverA, "a1"));
        _indexed.Remove((_serverA, "a1"));
        _index.Setup(index => index.Snapshot).Returns(() => new IndexSnapshot
        {
            Version = IndexSnapshot.CurrentVersion,
            Health = IndexHealth.Ok,
            Servers = [new ServerCatalog { ServerId = _serverA, State = ServerSyncState.Stale, Items = [] }]
        });
        AllPathsLinked();

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.NotNull(_registry.Find(old.Id));
    }

    [Fact]
    public async Task AnImportedLibraryReadEmptyHoldsTheOldStubsApartFromTheScanAndTheLogNamesIt()
    {
        _imported.Add("lib");
        var (old, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        _indexed.Remove((_serverA, "a1"));
        AllPathsLinked();
        _servers.Setup(servers => servers.GetLibrariesAsync(_serverA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(RemoteResult.Success<IReadOnlyList<RemoteLibrary>>([new RemoteLibrary("lib", "Film di casa", "movies")]));

        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);

        Assert.NotNull(_registry.Find(old.Id));
        Assert.True(_items.ContainsKey(oldItem.Id));
        var shown = migration.GetStatus(Root)!;
        Assert.Equal(1, shown.Held);
        Assert.Equal(0, shown.Waiting);
        var line = Assert.Single(_logger.Lines, line => line.Contains("Film di casa", StringComparison.Ordinal));
        Assert.Contains("Server", line, StringComparison.Ordinal);
        Assert.DoesNotContain("http", line, StringComparison.Ordinal);
        Assert.DoesNotContain(_logger.Lines, line => line.Contains(_serverA.ToString("N"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHeldLibraryWhoseNameCannotBeReadIsLoggedByServerAndId()
    {
        _imported.Add("lib");
        OldFilm("Dune", (_serverA, "a1"));
        _indexed.Remove((_serverA, "a1"));
        AllPathsLinked();
        _servers.Setup(servers => servers.GetLibrariesAsync(_serverA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(RemoteResult.Fail<IReadOnlyList<RemoteLibrary>>(RemoteFailure.Unreachable));

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        var line = Assert.Single(_logger.Lines, line => line.Contains("came back empty", StringComparison.Ordinal));
        Assert.Contains("Server", line, StringComparison.Ordinal);
        Assert.Contains("lib", line, StringComparison.Ordinal);
        Assert.DoesNotContain("http", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATitleStillOnAnotherServerWaitsAndIsNotHeld()
    {
        _imported.Add("lib");
        OldFilm("Dune", (_serverA, "a1"), (_serverB, "b1"));
        _indexed.Remove((_serverA, "a1"));
        AllPathsLinked();

        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);

        var shown = migration.GetStatus(Root)!;
        Assert.Equal(1, shown.Waiting);
        Assert.Equal(0, shown.Held);
    }

    [Fact]
    public async Task AnOldEpisodeOfAnImportedLibraryReadEmptyKeepsItsOldSeries()
    {
        _imported.Add("lib");
        var (show, showItem) = OldShow("Lost", (_serverA, "s1"));
        var (episode, episodeItem) = OldEpisode(show, (_serverA, "e1"));
        _stubs[_stubs.IndexOf(episode)] = episode with { Sources = [Source((_serverA, "e1")) with { LibraryId = "lib" }] };
        _indexed.Clear();
        AllPathsLinked();

        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);

        Assert.NotNull(_registry.Find(episode.Id));
        Assert.Contains(_registry.State.Series, series => series.Id == show.Id);
        Assert.True(_items.ContainsKey(episodeItem.Id) && _items.ContainsKey(showItem.Id));
        Assert.Equal(1, migration.GetStatus(Root)!.Held);
        Assert.Equal(0, migration.GetStatus(Root)!.Waiting);
    }

    [Fact]
    public async Task ATitleGoneFromAnImportedLibraryWithOtherItemsIsGone()
    {
        _imported.Add("lib");
        var (old, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        _indexed.Remove((_serverA, "a1"));
        _indexed.Add((_serverA, "other"));
        AllPathsLinked();

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.Null(_registry.Find(old.Id));
        Assert.False(_items.ContainsKey(oldItem.Id));
    }

    [Fact]
    public async Task AnOldSourceWithNoLibraryIsGoneEvenIfTheImportedLibraryReadsEmpty()
    {
        _imported.Add("lib");
        var (old, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        _stubs[_stubs.IndexOf(old)] = old with { Sources = [Source((_serverA, "a1"))] };
        _indexed.Remove((_serverA, "a1"));
        AllPathsLinked();

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.Null(_registry.Find(old.Id));
        Assert.False(_items.ContainsKey(oldItem.Id));
    }

    [Fact]
    public async Task ATitleOnlyOnARemovedServerIsGoneEvenWhileTheIndexStillListsIt()
    {
        var (old, oldItem) = OldFilm("Dune", (_serverB, "b1"));
        _configured.Remove(_serverB);
        _removed.Add(_serverB);
        AllPathsLinked();

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.Null(_registry.Find(old.Id));
        Assert.False(_items.ContainsKey(oldItem.Id));
    }

    [Fact]
    public async Task ATitleOnlyOnAServerMissingFromTheSettingsWithoutTheRemovalMarkWaits()
    {
        var (old, _) = OldFilm("Dune", (_serverB, "b1"));
        _configured.Remove(_serverB);
        _synced.Remove(_serverB);
        AllPathsLinked();

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.NotNull(_registry.Find(old.Id));
    }

    [Fact]
    public async Task ARefusedMoveIsClosedWithAWarningThatCountsOnly()
    {
        var (old, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        AllPathsLinked();
        _outcome = move => new WatchStateMoveOutcome(move, WatchStateMoveStatus.Refused, []);

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.Null(_registry.Find(old.Id));
        Assert.False(_items.ContainsKey(oldItem.Id));
        var warning = Assert.Single(_logger.Lines, line => line.Contains("refused", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Dune", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(Root, warning, StringComparison.Ordinal);
        Assert.Contains(LogLevel.Warning, _logger.Levels);
    }

    [Theory]
    [InlineData(WatchStateRefusal.OldItemNotStub)]
    [InlineData(WatchStateRefusal.NoTarget)]
    [InlineData(WatchStateRefusal.TargetNotStub)]
    [InlineData(WatchStateRefusal.SeveralLocalVersions)]
    [InlineData(WatchStateRefusal.LocalNotInGroup)]
    [InlineData(WatchStateRefusal.LocalNotPrimary)]
    public async Task ADefinitiveRefusalClosesTheTitle(WatchStateRefusal refusal)
    {
        var (old, _) = OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        AllPathsLinked();
        _outcome = move => new WatchStateMoveOutcome(move, WatchStateMoveStatus.Refused, []) { Refusal = refusal };

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.Null(_registry.Find(old.Id));
    }

    [Fact]
    public async Task ARefusalForAMissingFederatedFolderLeavesTheTitleWaiting()
    {
        var (old, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        AllPathsLinked();
        _outcome = move => new WatchStateMoveOutcome(move, WatchStateMoveStatus.Refused, []) { Refusal = WatchStateRefusal.NoFederatedFolder };

        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);

        Assert.NotNull(_registry.Find(old.Id));
        Assert.True(_items.ContainsKey(oldItem.Id));
        var shown = migration.GetStatus(Root)!;
        Assert.Equal(1, shown.Waiting);
        Assert.Equal(0, shown.Blocked);
        Assert.DoesNotContain(_logger.Lines, line => line.Contains("go without it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusalWhileTheLinkOfTheRoundFailedLeavesTheTitleWaitingUntilALinkSucceeds()
    {
        var (old, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        AllPathsLinked();
        _outcome = move => new WatchStateMoveOutcome(move, WatchStateMoveStatus.Refused, []) { Refusal = WatchStateRefusal.LocalNotPrimary };
        _linker.Setup(linker => linker.LastRunFailed).Returns(true);

        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);

        Assert.NotNull(_registry.Find(old.Id));
        Assert.True(_items.ContainsKey(oldItem.Id));
        Assert.Equal(1, migration.GetStatus(Root)!.Waiting);

        _linker.Setup(linker => linker.LastRunFailed).Returns(false);
        await migration.RunAsync(CancellationToken.None);

        Assert.Null(_registry.Find(old.Id));
    }

    [Fact]
    public async Task AFederatedFolderChangedDuringTheRunLeavesTheOldTitlesWaiting()
    {
        var (old, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var a = NewFilm(_serverA, "a1");
        AllPathsLinked();
        Row(_mario, oldItem).IsFavorite = true;
        var moved = Path.Combine(_data, "altrove");

        // The admin saves another folder once the migration has read the first one.
        _library.Setup(library => library.GetVirtualFolders()).Returns(() =>
        {
            _store.Configuration.FederatedFolderPath = moved;
            return _libraries.ToList();
        });

        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);

        Assert.NotNull(_registry.Find(old.Id));
        Assert.True(_items.ContainsKey(oldItem.Id));
        Assert.False(Row(_mario, a).IsFavorite);
        Assert.DoesNotContain(_logger.Lines, line => line.Contains("go without it", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(WatchStateMoveStatus.Failed, null)]
    [InlineData(WatchStateMoveStatus.UsersLeft, WatchStateUserSkipReason.NoUserData)]
    [InlineData(WatchStateMoveStatus.UsersLeft, WatchStateUserSkipReason.NotSaved)]
    public async Task AMoveThatAScanCannotFixKeepsTheOldStubsAndShowsAsBlocked(WatchStateMoveStatus status, WatchStateUserSkipReason? reason)
    {
        var (old, _) = OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        AllPathsLinked();
        _outcome = move => new WatchStateMoveOutcome(move, status, reason is { } why ? [new WatchStateUserSkip(_mario.Id, why)] : []);

        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);

        Assert.NotNull(_registry.Find(old.Id));
        var shown = migration.GetStatus(Root)!;
        Assert.Equal(1, shown.Blocked);
        Assert.Equal(0, shown.Waiting);
    }

    [Fact]
    public async Task AMoveWaitingForAScanIsNotBlocked()
    {
        OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        AllPathsLinked();
        _outcome = move => new WatchStateMoveOutcome(move, WatchStateMoveStatus.NotFound, []);

        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);

        var shown = migration.GetStatus(Root)!;
        Assert.Equal(1, shown.Waiting);
        Assert.Equal(0, shown.Blocked);
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
    public async Task APrimaryTheAdminChoseForTheLocalCopyTakesTheFavoriteOfTheOldStub()
    {
        var (_, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var a = NewFilm(_serverA, "a1");
        var local = Add(new Movie { Path = "/media/film/Dune.mkv" });
        var chosen = Add(new Movie { Path = "/media/film/Dune Extended.mkv" });
        Group(local, a);

        // The admin merged the local copy into another film, and the linker put the stub under that film (row 162).
        local.SetPrimaryVersionId(chosen.Id);
        a.SetPrimaryVersionId(chosen.Id);
        chosen.LinkedAlternateVersions = new[] { local, a }.Select(version => new LinkedChild { ItemId = version.Id, Type = LinkedChildType.LinkedAlternateVersion }).ToArray();
        AllPathsLinked();
        Row(_mario, oldItem).IsFavorite = true;

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.True(Row(_mario, chosen).IsFavorite);
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
            Sources = sources.Select(source => Source(source) with { LibraryId = "lib" }).ToList()
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
        await _registry.SaveAsync(StubState.Empty with { Stubs = _stubs.ToList(), Series = _series.ToList(), Groups = _groups.ToList(), RemovedServers = _removed.ToList() }, CancellationToken.None);
        IWatchStateCarrier carrier = new WatchStateCarrier(_registry, _store, _library.Object, _users.Object, _userData.Object, _lastServed, _log, new CapturingLogger<WatchStateCarrier>());
        if (_outcome is { } outcome)
        {
            var fake = new Mock<IWatchStateCarrier>();
            fake.Setup(carry => carry.CarryAsync(It.IsAny<IReadOnlyList<WatchStateMove>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<WatchStateMove> moves, CancellationToken _) => new WatchStateCarryReport(moves.Select(outcome).ToList()));
            carrier = fake.Object;
        }

        return new LayoutMigration(_registry, _store, _library.Object, _servers.Object, _index.Object, carrier, new StubRemover(_library.Object), _linker.Object, _logger);
    }

    private UserItemData Row(User user, BaseItem item) => _userData.Object.GetUserData(user, item)!;
}
