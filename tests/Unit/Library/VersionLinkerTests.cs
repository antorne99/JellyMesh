using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;
using LinkedChildType = MediaBrowser.Controller.Entities.LinkedChildType;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class VersionLinkerTests : IDisposable
{
    private static readonly DateTime _older = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _newer = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid _serverA = Guid.NewGuid();
    private static readonly Guid _serverB = Guid.NewGuid();
    private static readonly Guid _serverC = Guid.NewGuid();

    private readonly string _data = Path.Combine(Path.GetTempPath(), "jellymesh-linker-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryConfigurationStore _store = new();
    private readonly StubRegistry _registry;
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Mock<IFederationIndex> _index = new();
    private readonly CapturingLogger<VersionLinker> _logger = new();
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly Dictionary<(Guid User, Guid Item), UserItemData> _data_ = [];
    private readonly List<BaseItem> _boxSets = [];
    private readonly List<(Guid Item, ItemUpdateType Reason)> _saves = [];
    private readonly List<ServerCatalog> _catalogs = [];
    private readonly User _user = new("mario", "auth", "reset");
    private readonly VersionLinker _linker;

    public VersionLinkerTests()
    {
        _store.Configuration.FederatedFolderPath = Path.Combine(_data, "federata");
        _registry = new StubRegistry(Path.Combine(_data, "plugin"), _store, new CapturingLogger<StubRegistry>());

        _library.Setup(library => library.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns((string key, Type type) => IdOf(key, type));
        _library.Setup(library => library.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
        _library.Setup(library => library.FindByPath(It.IsAny<string>(), It.IsAny<bool?>()))
            .Returns((string path, bool? _) => _items.Values.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.Ordinal)));
        _library.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) => query.IncludeItemTypes.SequenceEqual([BaseItemKind.BoxSet]) ? _boxSets.ToList() : []);
        _library.Setup(library => library.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Callback((BaseItem item, BaseItem _, ItemUpdateType reason, CancellationToken _) => _saves.Add((item.Id, reason)))
            .Returns(Task.CompletedTask);

        _users.Setup(users => users.GetUsers()).Returns(() => [_user]);
        _userData.Setup(data => data.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((User user, BaseItem item) =>
            {
                if (!_data_.TryGetValue((user.Id, item.Id), out var data))
                {
                    data = new UserItemData { Key = item.Id.ToString("N") };
                    _data_[(user.Id, item.Id)] = data;
                }

                return data;
            });

        _index.Setup(index => index.Snapshot).Returns(() => new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = IndexHealth.Ok, Servers = _catalogs.ToList() });

        _linker = new VersionLinker(_registry, _index.Object, _store, _library.Object, _users.Object, _userData.Object, new StubItemUpdater(_library.Object), _logger);
    }

    private string Root => _store.Configuration.FederatedFolderPath;

    public void Dispose()
    {
        _registry.Dispose();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, true);
        }
    }

    [Fact]
    public async Task TwoServerStubsAreLinkedWithTheBestAsPrimary()
    {
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 2160);
        await SaveAsync([a, b], Group("g", ["sa", "sb"]));
        var itemA = Scanned(a);
        var itemB = Scanned(b);

        await _linker.LinkAsync(false, CancellationToken.None);

        Assert.Equal(itemB.Id, itemA.PrimaryVersionId);
        Assert.Null(itemB.PrimaryVersionId);
        Assert.Equal([itemA.Id], LinkedIds(itemB));
        Assert.Equal([(itemA.Id, ItemUpdateType.MetadataImport), (itemB.Id, ItemUpdateType.MetadataImport)], _saves);
        Assert.Equal("sb", _registry.State.Groups.Single().PrimaryStubId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AGroupWithAStubWhoseRenameIsPendingIsLeftAsItIs(bool thorough)
    {
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 2160);
        var c = Stub("sc", _serverC, 720);
        await _registry.SaveAsync(
            StubState.Empty with { Stubs = [a, b, c], Groups = [Group("g", ["sa", "sb"])], Renames = [new StubRename("sa", "old.strm"), new StubRename("sc", "old2.strm")] },
            CancellationToken.None);
        var itemA = Scanned(a);
        var itemB = Scanned(b);
        var itemC = Scanned(c);
        itemC.SetPrimaryVersionId(itemA.Id);

        await _linker.LinkAsync(thorough, CancellationToken.None);

        Assert.Null(itemA.PrimaryVersionId);
        Assert.Null(itemB.PrimaryVersionId);
        Assert.Equal(itemA.Id, itemC.PrimaryVersionId);
        Assert.Empty(_saves);
        Assert.Null(_registry.State.Groups.Single().PrimaryStubId);
    }

    [Fact]
    public async Task TheLocalCopyIsThePrimary()
    {
        var local = Local();
        var a = Stub("sa", _serverA, 2160);
        var b = Stub("sb", _serverB, 1080);
        await SaveAsync([a, b], Group("g", ["sa", "sb"], [local.Id], primary: "sa"));
        var itemA = Scanned(a);
        var itemB = Scanned(b);

        await _linker.LinkAsync(false, CancellationToken.None);

        Assert.Null(local.PrimaryVersionId);
        Assert.Equal(new[] { itemA.Id, itemB.Id }.Order(), LinkedIds(local).Order());
        Assert.Equal(local.Id, itemA.PrimaryVersionId);
        Assert.Equal(local.Id, itemB.PrimaryVersionId);
        Assert.Equal(local.Id, _saves.Last().Item);
        Assert.Null(_registry.State.Groups.Single().PrimaryStubId);
    }

    [Fact]
    public async Task OnlyTheFirstLocalCopyIsLinked()
    {
        var first = Local();
        var second = Local(played: true);
        var a = Stub("sa", _serverA, 1080);
        await SaveAsync([a], Group("g", ["sa"], [first.Id, second.Id]));
        var itemA = Scanned(a);
        var collection = Add(new BoxSet { LinkedChildren = [LinkedChild.Create(second)] }, Guid.NewGuid());
        _boxSets.Add(collection);

        await _linker.LinkAsync(true, CancellationToken.None);

        Assert.Equal(first.Id, itemA.PrimaryVersionId);
        Assert.Equal([itemA.Id], LinkedIds(first));
        Assert.Null(second.PrimaryVersionId);
        Assert.Empty(second.LinkedAlternateVersions);
        Assert.DoesNotContain(_saves, save => save.Item == second.Id || save.Item == collection.Id);
        Assert.False(Data(first).Played);
        _userData.Verify(data => data.SaveUserData(It.IsAny<User>(), second, It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal([second.Id], ((Folder)collection).LinkedChildren.Select(child => child.ItemId!.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFilmTheAdminMergedWithTheLocalCopyStaysLinked(bool thorough)
    {
        var local = Local();
        var other = Local();
        other.SetPrimaryVersionId(local.Id);
        local.LinkedAlternateVersions = [Link(other.Id)];
        var a = Stub("sa", _serverA, 1080);
        await SaveAsync([a], Group("g", ["sa"], [local.Id]));
        var itemA = Scanned(a);

        await _linker.LinkAsync(thorough, CancellationToken.None);
        await _linker.LinkAsync(thorough, CancellationToken.None);

        Assert.Equal(local.Id, itemA.PrimaryVersionId);
        Assert.Equal(new[] { itemA.Id, other.Id }.Order(), LinkedIds(local).Order());
        Assert.Equal(local.Id, other.PrimaryVersionId);
        Assert.DoesNotContain(_saves, save => save.Item == other.Id);
    }

    [Fact]
    public async Task TheStubsFollowTheFilmTheLocalCopyWasMergedInto()
    {
        var local = Local();
        var other = Local();
        local.SetPrimaryVersionId(other.Id);
        other.LinkedAlternateVersions = [Link(local.Id)];
        var a = Stub("sa", _serverA, 1080);
        await SaveAsync([a], Group("g", ["sa"], [local.Id]));
        var itemA = Scanned(a);

        await _linker.LinkAsync(true, CancellationToken.None);

        Assert.Equal(other.Id, itemA.PrimaryVersionId);
        Assert.Equal(new[] { itemA.Id, local.Id }.Order(), LinkedIds(other).Order());
        Assert.Equal(other.Id, local.PrimaryVersionId);
        Assert.DoesNotContain(_saves, save => save.Item == local.Id);
    }

    // Q170: two groups whose local copies the admin merged into one film; the first by group ID links to it.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OnlyTheFirstGroupLinksToAFilmTwoGroupsShare(bool thorough, bool reversed)
    {
        var film = Local();
        var first = Local();
        var second = Local();
        first.SetPrimaryVersionId(film.Id);
        second.SetPrimaryVersionId(film.Id);
        film.LinkedAlternateVersions = [Link(first.Id), Link(second.Id)];
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 2160);
        var c = Stub("sc", _serverC, 720);
        VersionGroup[] groups = [Group("g1", ["sa"], [first.Id]), Group("g2", ["sb", "sc"], [second.Id])];
        await SaveAsync([a, b, c], reversed ? [.. groups.Reverse()] : groups);
        var (itemA, itemB, itemC) = (Scanned(a), Scanned(b), Scanned(c));

        await _linker.LinkAsync(thorough, CancellationToken.None);
        _saves.Clear();
        _userData.Invocations.Clear();
        await _linker.LinkAsync(thorough, CancellationToken.None);

        Assert.Equal(film.Id, itemA.PrimaryVersionId);
        Assert.Equal(new[] { first.Id, second.Id, itemA.Id }.Order(), LinkedIds(film).Order());
        Assert.Null(itemB.PrimaryVersionId);
        Assert.Equal(itemB.Id, itemC.PrimaryVersionId);
        Assert.Equal([itemC.Id], LinkedIds(itemB));
        Assert.Equal(film.Id, second.PrimaryVersionId);
        Assert.Empty(_saves);
        _userData.Verify(data => data.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ALinkOfOneTitleLinksOnlyTheGroupsOfItsLocalCopies()
    {
        var local = Local();
        var (a, b, c) = (Stub("sa", _serverA, 1080), Stub("sb", _serverB, 2160), Stub("sc", _serverC, 720));
        await SaveAsync([a, b, c], Group("g1", ["sa"], [local.Id]), Group("g2", ["sb", "sc"]));
        var (itemA, itemB, itemC) = (Scanned(a), Scanned(b), Scanned(c));

        await _linker.LinkLocalAsync([local.Id], CancellationToken.None);

        Assert.Equal(local.Id, itemA.PrimaryVersionId);
        Assert.Equal([itemA.Id], LinkedIds(local));
        Assert.Null(itemB.PrimaryVersionId);
        Assert.Null(itemC.PrimaryVersionId);
        Assert.Empty(LinkedIds(itemB));
    }

    // Q170 holds for one title too: the group with the lower ID claims the film, as in the full run.
    [Fact]
    public async Task ALinkOfOneTitleLetsTheFirstGroupClaimAFilmTwoGroupsShare()
    {
        var film = Local();
        var first = Local();
        var second = Local();
        first.SetPrimaryVersionId(film.Id);
        second.SetPrimaryVersionId(film.Id);
        film.LinkedAlternateVersions = [Link(first.Id), Link(second.Id)];
        var (a, b, c) = (Stub("sa", _serverA, 1080), Stub("sb", _serverB, 2160), Stub("sc", _serverC, 720));
        await SaveAsync([a, b, c], Group("g1", ["sa"], [first.Id]), Group("g2", ["sb", "sc"], [second.Id]));
        var (itemA, itemB, itemC) = (Scanned(a), Scanned(b), Scanned(c));

        await _linker.LinkLocalAsync([second.Id], CancellationToken.None);

        Assert.Equal(film.Id, itemA.PrimaryVersionId);
        Assert.Null(itemB.PrimaryVersionId);
        Assert.Equal(itemB.Id, itemC.PrimaryVersionId);
    }

    [Fact]
    public async Task ALinkOfNoLocalCopyDoesNothing()
    {
        var (a, b) = (Stub("sa", _serverA, 1080), Stub("sb", _serverB, 2160));
        await SaveAsync([a, b], Group("g", ["sa", "sb"]));
        Scanned(a);
        Scanned(b);

        await _linker.LinkLocalAsync([], CancellationToken.None);

        Assert.Empty(_saves);
    }

    [Fact]
    public async Task AFileVersionOfTheLocalCopyStaysLinked()
    {
        var local = Local();
        var version = Add(new Movie { Path = "/media/films/x - 720p.mkv", OwnerId = local.Id }, Guid.NewGuid());
        version.SetPrimaryVersionId(local.Id);
        local.LinkedAlternateVersions = [Link(version.Id)];
        var a = Stub("sa", _serverA, 1080);
        await SaveAsync([a], Group("g", ["sa"], [local.Id]));
        var itemA = Scanned(a);

        await _linker.LinkAsync(true, CancellationToken.None);

        Assert.Equal(new[] { itemA.Id, version.Id }.Order(), LinkedIds(local).Order());
        Assert.Equal(local.Id, version.PrimaryVersionId);
        Assert.DoesNotContain(_saves, save => save.Item == version.Id);
    }

    [Fact]
    public async Task NoMetadataSaverRunsOnTheLocalCopy()
    {
        var local = Local();
        var a = Stub("sa", _serverA, 1080);
        await SaveAsync([a], Group("g", ["sa"], [local.Id]));
        Scanned(a);

        await _linker.LinkAsync(false, CancellationToken.None);

        Assert.NotEmpty(_saves);
        Assert.All(_saves, save => Assert.Equal(ItemUpdateType.MetadataImport, save.Reason));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASecondRunChangesNothing(bool thorough)
    {
        var local = Local(played: true);
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 1080);
        var lone = Stub("sl", _serverC, 1080);
        await SaveAsync([a, b, lone], Group("g", ["sa", "sb"], [local.Id]));
        Scanned(a);
        Scanned(b);
        Scanned(lone);
        await _linker.LinkAsync(false, CancellationToken.None);
        _saves.Clear();
        _userData.Invocations.Clear();
        _library.Invocations.Clear();

        await _linker.LinkAsync(thorough, CancellationToken.None);

        Assert.Empty(_saves);
        _userData.Verify(data => data.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()), Times.Never);
        _userData.Verify(data => data.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()), Times.Never);
        if (!thorough)
        {
            _library.Verify(library => library.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
        }
    }

    [Fact]
    public async Task AGroupTheAdminSplitIsLinkedAgain()
    {
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 2160);
        await SaveAsync([a, b], Group("g", ["sa", "sb"]));
        var itemA = Scanned(a);
        var itemB = Scanned(b);
        await _linker.LinkAsync(false, CancellationToken.None);

        // What "Split versions" does: every member loses its link.
        itemA.SetPrimaryVersionId(null);
        itemB.LinkedAlternateVersions = [];
        await _linker.LinkAsync(false, CancellationToken.None);

        Assert.Equal(itemB.Id, itemA.PrimaryVersionId);
        Assert.Equal([itemA.Id], LinkedIds(itemB));
    }

    [Fact]
    public async Task AMergeTheAdminMadeIsUndoneForTheGroup()
    {
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 2160);
        await SaveAsync([a, b], Group("g", ["sa", "sb"]));
        var itemA = Scanned(a);
        var itemB = Scanned(b);
        var other = Local();
        await _linker.LinkAsync(false, CancellationToken.None);

        // MergeVersions of the group with another film.
        other.SetPrimaryVersionId(itemB.Id);
        itemB.LinkedAlternateVersions = [Link(itemA.Id), Link(other.Id)];
        await _linker.LinkAsync(false, CancellationToken.None);

        Assert.Null(other.PrimaryVersionId);
        Assert.Equal([itemA.Id], LinkedIds(itemB));
    }

    [Fact]
    public async Task AGroupWithOneItemIsLeftAlone()
    {
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 2160);
        await SaveAsync([a, b], Group("g", ["sa", "sb"]));
        Scanned(a);

        await _linker.LinkAsync(false, CancellationToken.None);

        Assert.Empty(_saves);
        Assert.Null(_registry.State.Groups.Single().PrimaryStubId);
    }

    [Fact]
    public async Task TheRecordedPrimaryStaysWhenABetterCopyJoins()
    {
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 1080);
        var c = Stub("sc", _serverC, 2160);
        await SaveAsync([a, b, c], Group("g", ["sa", "sb", "sc"], primary: "sa"));
        var itemA = Scanned(a);
        var itemC = Scanned(c);
        Scanned(b);

        await _linker.LinkAsync(false, CancellationToken.None);

        Assert.Equal(itemA.Id, itemC.PrimaryVersionId);
        Assert.Equal("sa", _registry.State.Groups.Single().PrimaryStubId);
    }

    [Fact]
    public async Task EpisodesAreLinkedLikeFilms()
    {
        var a = Stub("ea", _serverA, 1080, StubKind.Episode);
        var b = Stub("eb", _serverB, 720, StubKind.Episode);
        await SaveAsync([a, b], Group("g", ["ea", "eb"]));
        var itemA = Scanned(a);
        var itemB = Scanned(b);

        await _linker.LinkAsync(false, CancellationToken.None);

        Assert.IsType<Episode>(itemA);
        Assert.Equal(itemA.Id, itemB.PrimaryVersionId);
    }

    [Fact]
    public async Task AStubFoundOnlyByPathIsLinkedByTheNightlyRun()
    {
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 720);
        await SaveAsync([a, b], Group("g", ["sa", "sb"]));
        var itemA = Scanned(a);

        // A stub in a mixed library is a plain video, so its ID is not the one of a film.
        var itemB = Add(new Video { Path = FederatedPaths.Resolve(Root, b.RelativePath) }, Guid.NewGuid());

        await _linker.LinkAsync(false, CancellationToken.None);
        Assert.Empty(_saves);

        await _linker.LinkAsync(true, CancellationToken.None);
        Assert.Equal(itemA.Id, itemB.PrimaryVersionId);
    }

    [Fact]
    public async Task APlayedLocalCopyMarksTheStubsItJoins()
    {
        var local = Local(played: true);
        var a = Stub("sa", _serverA, 1080);
        await SaveAsync([a], Group("g", ["sa"], [local.Id]));
        var itemA = Scanned(a);
        Data(itemA).PlaybackPositionTicks = 500;

        await _linker.LinkAsync(false, CancellationToken.None);

        Assert.True(Data(itemA).Played);
        Assert.Equal(500, Data(itemA).PlaybackPositionTicks);
        Assert.Equal(0, Data(itemA).PlayCount);
        _userData.Verify(data => data.SaveUserData(_user, itemA, It.IsAny<UserItemData>(), UserDataSaveReason.Import, It.IsAny<CancellationToken>()), Times.Once);
        _userData.Verify(data => data.SaveUserData(_user, local, It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TheVersionPlayedLastDecidesPlayed()
    {
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 2160);
        await SaveAsync([a, b], Group("g", ["sa", "sb"]));
        var itemA = Scanned(a);
        var itemB = Scanned(b);
        Data(itemA).Played = true;
        Data(itemA).LastPlayedDate = _older;
        Data(itemB).LastPlayedDate = _newer;
        Data(itemB).PlaybackPositionTicks = 10;

        await _linker.LinkAsync(false, CancellationToken.None);

        Assert.False(Data(itemA).Played);
        Assert.Equal(10, Data(itemB).PlaybackPositionTicks);
    }

    [Fact]
    public async Task CollectionsMoveToThePrimaryAndPlaylistsStay()
    {
        var local = Local();
        var a = Stub("sa", _serverA, 1080);
        await SaveAsync([a], Group("g", ["sa"], [local.Id]));
        var itemA = Scanned(a);
        var other = Local();
        var collection = Add(new BoxSet { LinkedChildren = [LinkedChild.Create(other), LinkedChild.Create(itemA)] }, Guid.NewGuid());
        var both = Add(new BoxSet { LinkedChildren = [LinkedChild.Create(itemA), LinkedChild.Create(local)] }, Guid.NewGuid());
        var playlist = Add(new Playlist { LinkedChildren = [LinkedChild.Create(itemA)] }, Guid.NewGuid());
        _boxSets.AddRange([collection, both]);

        await _linker.LinkAsync(false, CancellationToken.None);

        Assert.Equal([other.Id, local.Id], ((Folder)collection).LinkedChildren.Select(child => child.ItemId!.Value));
        Assert.Equal([local.Id], ((Folder)both).LinkedChildren.Select(child => child.ItemId!.Value));
        Assert.Equal([itemA.Id], ((Folder)playlist).LinkedChildren.Select(child => child.ItemId!.Value));
        Assert.Contains((collection.Id, ItemUpdateType.MetadataEdit), _saves);
        Assert.DoesNotContain(_saves, save => save.Item == playlist.Id);
        _library.Verify(library => library.RerouteLinkedChildReferencesAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ARunThatCannotLinkAGroupCountsAsFailedUntilARunLinksThemAll()
    {
        var local = Local();
        var a = Stub("sa", _serverA, 1080);
        await SaveAsync([a], Group("g", ["sa"], [local.Id]));
        var itemA = Scanned(a);
        _library.Setup(library => library.UpdateItemAsync(It.Is<BaseItem>(item => item.Id == itemA.Id), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException(TestValues.Host));

        await _linker.LinkAsync(false, CancellationToken.None);
        Assert.True(_linker.LastRunFailed);

        _library.Setup(library => library.UpdateItemAsync(It.Is<BaseItem>(item => item.Id == itemA.Id), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        await _linker.LinkAsync(false, CancellationToken.None);

        Assert.Equal(local.Id, itemA.PrimaryVersionId);
        Assert.False(_linker.LastRunFailed);
    }

    // Q324: after a restart no full run has finished yet, so a refusal cannot be taken as final.
    [Fact]
    public async Task TheFailedFlagIsUpUntilTheFirstFullRunEnds()
    {
        var local = Local();
        var a = Stub("sa", _serverA, 1080);
        await SaveAsync([a], Group("g", ["sa"], [local.Id]));
        Scanned(a);

        Assert.True(_linker.LastRunFailed);
        await _linker.LinkLocalAsync([local.Id], CancellationToken.None);
        Assert.True(_linker.LastRunFailed);

        await _linker.LinkAsync(false, CancellationToken.None);
        Assert.False(_linker.LastRunFailed);
    }

    // Q324: a scoped run that fails outside the catch of a group raises the flag too.
    [Fact]
    public async Task AScopedRunThatFailsOutsideAGroupRaisesTheFailedFlag()
    {
        var local = Local();
        var a = Stub("sa", _serverA, 1080);
        await SaveAsync([a], Group("g", ["sa"], [local.Id]));
        await _linker.LinkAsync(false, CancellationToken.None);
        Assert.False(_linker.LastRunFailed);
        Scanned(a);
        _library.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>())).Throws(new IOException(TestValues.Host));

        await Assert.ThrowsAsync<IOException>(() => _linker.LinkLocalAsync([local.Id], CancellationToken.None));

        Assert.True(_linker.LastRunFailed);
    }

    // Q324: a nightly run that cannot detach a stub out of every group counts as failed.
    [Fact]
    public async Task ANightlyRunThatCannotDetachAStubRaisesTheFailedFlag()
    {
        var a = Stub("sa", _serverA, 1080);
        await SaveAsync([a]);
        var itemA = Scanned(a);
        var local = Local();
        itemA.SetPrimaryVersionId(local.Id);
        local.LinkedAlternateVersions = [Link(itemA.Id)];
        _library.Setup(library => library.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException(TestValues.Host));

        await _linker.LinkAsync(true, CancellationToken.None);

        Assert.True(_linker.LastRunFailed);
    }

    [Fact]
    public async Task ACollectionWhoseSaveFailsIsPutBackAndTheOthersStillMove()
    {
        var local = Local();
        var a = Stub("sa", _serverA, 1080);
        await SaveAsync([a], Group("g", ["sa"], [local.Id]));
        var itemA = Scanned(a);
        var failing = Add(new BoxSet { LinkedChildren = [LinkedChild.Create(itemA)] }, Guid.NewGuid());
        var other = Add(new BoxSet { LinkedChildren = [LinkedChild.Create(itemA)] }, Guid.NewGuid());
        _boxSets.AddRange([failing, other]);
        _library.Setup(library => library.UpdateItemAsync(It.Is<BaseItem>(item => item.Id == failing.Id), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException(TestValues.Host));

        await _linker.LinkAsync(false, CancellationToken.None);

        Assert.Equal(local.Id, itemA.PrimaryVersionId);
        Assert.Equal([itemA.Id], ((Folder)failing).LinkedChildren.Select(child => child.ItemId!.Value));
        Assert.Equal([local.Id], ((Folder)other).LinkedChildren.Select(child => child.ItemId!.Value));
        Assert.DoesNotContain(_logger.Lines, line => line.Contains(TestValues.Host, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALoneMemberOfAGroupIsDetachedOnlyByTheNightlyRun()
    {
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 2160);
        await SaveAsync([a, b], Group("g", ["sa", "sb"], primary: "sb"));
        var itemA = Scanned(a);
        var film = Local();
        var other = Local();
        itemA.SetPrimaryVersionId(film.Id);
        other.SetPrimaryVersionId(film.Id);
        film.LinkedAlternateVersions = [Link(itemA.Id), Link(other.Id)];

        await _linker.LinkAsync(false, CancellationToken.None);
        Assert.Empty(_saves);

        await _linker.LinkAsync(true, CancellationToken.None);

        Assert.Null(itemA.PrimaryVersionId);
        Assert.Equal([other.Id], LinkedIds(film));
        Assert.Equal(film.Id, other.PrimaryVersionId);
        Assert.DoesNotContain(_saves, save => save.Item == other.Id);
        Assert.Equal("sb", _registry.State.Groups.Single().PrimaryStubId);
    }

    [Fact]
    public async Task ALoneLocalCopyKeepsOnlyItsLinksToOtherFilms()
    {
        var local = Local();
        var other = Local();
        var a = Stub("sa", _serverA, 1080);
        var gone = Stub("sg", _serverB, 1080);
        await SaveAsync([a], Group("g", ["sa"], [local.Id]));
        var itemGone = Scanned(gone);
        itemGone.SetPrimaryVersionId(local.Id);
        other.SetPrimaryVersionId(local.Id);
        local.LinkedAlternateVersions = [Link(itemGone.Id), Link(other.Id)];

        await _linker.LinkAsync(false, CancellationToken.None);
        Assert.Empty(_saves);

        await _linker.LinkAsync(true, CancellationToken.None);

        Assert.Null(itemGone.PrimaryVersionId);
        Assert.Equal([other.Id], LinkedIds(local));
        Assert.Equal(local.Id, other.PrimaryVersionId);
        Assert.DoesNotContain(_saves, save => save.Item == other.Id);
    }

    [Fact]
    public async Task StubsInNoGroupAreDetachedOnlyByTheNightlyRun()
    {
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 2160);
        await SaveAsync([a, b]);
        var itemA = Scanned(a);
        var itemB = Scanned(b);
        itemA.SetPrimaryVersionId(itemB.Id);
        itemB.LinkedAlternateVersions = [Link(itemA.Id)];

        await _linker.LinkAsync(false, CancellationToken.None);
        Assert.Empty(_saves);

        await _linker.LinkAsync(true, CancellationToken.None);

        Assert.Null(itemA.PrimaryVersionId);
        Assert.Null(itemB.PrimaryVersionId);
        Assert.Empty(itemB.LinkedAlternateVersions);
        Assert.All(_saves, save => Assert.Equal(ItemUpdateType.MetadataImport, save.Reason));
        Assert.Equal(2, _saves.Count);
    }

    [Fact]
    public async Task AStubThatLeftEveryGroupLeavesTheLocalCopy()
    {
        var a = Stub("sa", _serverA, 1080);
        await SaveAsync([a]);
        var itemA = Scanned(a);
        var local = Local();
        var other = Local();
        itemA.SetPrimaryVersionId(local.Id);
        other.SetPrimaryVersionId(local.Id);
        local.LinkedAlternateVersions = [Link(itemA.Id), Link(other.Id)];

        await _linker.LinkAsync(true, CancellationToken.None);

        Assert.Null(itemA.PrimaryVersionId);
        Assert.Equal([other.Id], LinkedIds(local));
        Assert.Equal(local.Id, other.PrimaryVersionId);
        Assert.DoesNotContain(_saves, save => save.Item == other.Id);
    }

    // Q171: an undo detaches at once the stubs of the group it dissolved, and no other stub out of every group (Q144).
    [Fact]
    public async Task ADetachAfterAnUndoTouchesOnlyTheStubsItNames()
    {
        var (a, b, c, d, e, f) = (Stub("sa", _serverA, 1080), Stub("sb", _serverB, 2160), Stub("sc", _serverA, 720), Stub("sd", _serverB, 720), Stub("se", _serverA, 480), Stub("sf", _serverB, 480));
        await SaveAsync([a, b, c, d, e, f], Group("g", ["se", "sf"]));
        var (itemA, itemB, itemC, itemD, itemE, itemF) = (Scanned(a), Scanned(b), Scanned(c), Scanned(d), Scanned(e), Scanned(f));
        foreach (var (primary, alternate) in new[] { (itemB, itemA), (itemD, itemC), (itemF, itemE) })
        {
            alternate.SetPrimaryVersionId(primary.Id);
            primary.LinkedAlternateVersions = [Link(alternate.Id)];
        }

        await _linker.DetachAsync(["sa", "sb", "se", "sf"], CancellationToken.None);

        Assert.Null(itemA.PrimaryVersionId);
        Assert.Empty(itemB.LinkedAlternateVersions);
        Assert.Equal(itemD.Id, itemC.PrimaryVersionId);
        Assert.Equal([itemC.Id], LinkedIds(itemD));
        Assert.Equal(itemF.Id, itemE.PrimaryVersionId);
        Assert.Equal([itemE.Id], LinkedIds(itemF));
        Assert.Equal(new[] { itemA.Id, itemB.Id }.Order(), _saves.Select(save => save.Item).Order());
    }

    [Fact]
    public async Task ADetachAfterAnUndoLeavesALinkBetweenLocalFilms()
    {
        var a = Stub("sa", _serverA, 1080);
        await SaveAsync([a]);
        var itemA = Scanned(a);
        var local = Local();
        var other = Local();
        itemA.SetPrimaryVersionId(local.Id);
        other.SetPrimaryVersionId(local.Id);
        local.LinkedAlternateVersions = [Link(itemA.Id), Link(other.Id)];

        await _linker.DetachAsync(["sa"], CancellationToken.None);

        Assert.Null(itemA.PrimaryVersionId);
        Assert.Equal([other.Id], LinkedIds(local));
        Assert.Equal(local.Id, other.PrimaryVersionId);
    }

    [Fact]
    public async Task TheStubsGroupedWithSomeCopiesAreTheStubsOfTheirGroups()
    {
        var local = Local();
        var (a, b, c, d, e) = (Stub("sa", _serverA, 1080), Stub("sb", _serverB, 2160), Stub("sc", _serverA, 720), Stub("sd", _serverB, 720), Stub("se", _serverC, 720));
        await SaveAsync([a, b, c, d, e], Group("g1", ["sa", "sb"]), Group("g2", ["sc", "sd"], [local.Id]), Group("g3", ["se"], [Local().Id]));
        MergedCopy[] copies =
        [
            new() { ServerId = _serverA.ToString("N"), ItemId = "ITEM-SA" },
            new() { ServerId = "local", ItemId = local.Id.ToString("N"), IsLocal = true },
            new() { ServerId = _serverC.ToString("N"), ItemId = "item-gone" }
        ];

        Assert.Equal(["sa", "sb", "sc", "sd"], _linker.GroupedStubsOf(copies).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheStubsGroupedWithASeriesCopyAreTheStubsOfItsEpisodesGroups()
    {
        var a1 = Stub("a1", _serverA, 1080, StubKind.Episode) with { SeriesStubId = "series-a" };
        var a2 = Stub("a2", _serverA, 1080, StubKind.Episode) with { SeriesStubId = "series-a" };
        var b1 = Stub("b1", _serverB, 1080, StubKind.Episode) with { SeriesStubId = "series-b" };
        var c1 = Stub("c1", _serverA, 1080, StubKind.Episode) with { SeriesStubId = "series-c" };
        var c2 = Stub("c2", _serverB, 1080, StubKind.Episode);
        var film = Stub("film", _serverA, 1080);
        var film2 = Stub("film2", _serverB, 1080);
        SeriesStub[] series =
        [
            new() { Id = "series-a", ServerId = _serverA, RelativePath = "a", WorkKey = "series|a", Sources = [new StubSource(_serverA, "SERIES-A", string.Empty)] },
            new() { Id = "series-b", ServerId = _serverB, RelativePath = "b", WorkKey = "series|b", Sources = [new StubSource(_serverB, "series-a", string.Empty)] },
            new() { Id = "series-c", ServerId = _serverA, RelativePath = "c", WorkKey = "series|c", Sources = [new StubSource(_serverA, "series-c", string.Empty)] }
        ];
        await _registry.SaveAsync(
            StubState.Empty with
            {
                Stubs = [a1, a2, b1, c1, c2, film, film2],
                Series = series,
                Groups = [Group("g1", ["a1", "b1"]), Group("g2", ["c1", "c2"]), Group("g3", ["film", "film2"])]
            },
            CancellationToken.None);
        MergedCopy[] copies =
        [
            new() { ServerId = _serverA.ToString("N"), ItemId = "series-a", Kind = ContentKind.Episode },
            new() { ServerId = _serverA.ToString("N"), ItemId = "item-film", Kind = ContentKind.Episode }
        ];

        Assert.Equal(["a1", "b1"], _linker.GroupedStubsOf(copies).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AStubInNoGroupWithoutLinksIsNotSaved()
    {
        var a = Stub("sa", _serverA, 1080);
        await SaveAsync([a]);
        Scanned(a);

        await _linker.LinkAsync(true, CancellationToken.None);

        Assert.Empty(_saves);
    }

    [Fact]
    public async Task AFailedDetachDoesNotStopTheOthers()
    {
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 1080);
        var c = Stub("sc", _serverA, 1080);
        var d = Stub("sd", _serverB, 1080);
        await SaveAsync([a, b, c, d]);
        var itemA = Scanned(a);
        var itemB = Scanned(b);
        var itemC = Scanned(c);
        var itemD = Scanned(d);
        itemA.SetPrimaryVersionId(itemB.Id);
        itemB.LinkedAlternateVersions = [Link(itemA.Id)];
        itemC.SetPrimaryVersionId(itemD.Id);
        itemD.LinkedAlternateVersions = [Link(itemC.Id)];
        _library.Setup(library => library.UpdateItemAsync(It.Is<BaseItem>(item => item.Id == itemA.Id || item.Id == itemB.Id), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException(TestValues.Host));

        await _linker.LinkAsync(true, CancellationToken.None);

        Assert.Null(itemC.PrimaryVersionId);
        Assert.Empty(itemD.LinkedAlternateVersions);
        Assert.DoesNotContain(_logger.Lines, line => line.Contains(TestValues.Host, StringComparison.Ordinal));
        Assert.Contains(_logger.Lines, line => line.Contains(nameof(IOException), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACollectionEntryAddedAfterLinkingMovesOnTheNightlyRun()
    {
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 2160);
        await SaveAsync([a, b], Group("g", ["sa", "sb"]));
        var itemA = Scanned(a);
        var itemB = Scanned(b);
        await _linker.LinkAsync(false, CancellationToken.None);
        var other = Local();
        var collection = Add(new BoxSet { LinkedChildren = [LinkedChild.Create(itemA), LinkedChild.Create(other)] }, Guid.NewGuid());
        var both = Add(new BoxSet { LinkedChildren = [LinkedChild.Create(itemB), LinkedChild.Create(itemA)] }, Guid.NewGuid());
        var playlist = Add(new Playlist { LinkedChildren = [LinkedChild.Create(itemA)] }, Guid.NewGuid());
        _boxSets.AddRange([collection, both]);
        _saves.Clear();

        await _linker.LinkAsync(false, CancellationToken.None);
        Assert.Empty(_saves);

        await _linker.LinkAsync(true, CancellationToken.None);

        Assert.Equal([itemB.Id, other.Id], ((Folder)collection).LinkedChildren.Select(child => child.ItemId!.Value));
        Assert.Equal([itemB.Id], ((Folder)both).LinkedChildren.Select(child => child.ItemId!.Value));
        Assert.Equal([itemA.Id], ((Folder)playlist).LinkedChildren.Select(child => child.ItemId!.Value));
        Assert.Equal([(collection.Id, ItemUpdateType.MetadataEdit), (both.Id, ItemUpdateType.MetadataEdit)], _saves);
    }

    [Fact]
    public async Task ACollectionWhoseMoveFailedMovesOnTheNightlyRun()
    {
        var local = Local();
        var a = Stub("sa", _serverA, 1080);
        await SaveAsync([a], Group("g", ["sa"], [local.Id]));
        var itemA = Scanned(a);
        var collection = Add(new BoxSet { LinkedChildren = [LinkedChild.Create(itemA)] }, Guid.NewGuid());
        _boxSets.Add(collection);
        var failing = true;
        _library.Setup(library => library.UpdateItemAsync(It.Is<BaseItem>(item => item.Id == collection.Id), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Returns(() => failing ? Task.FromException(new IOException()) : Task.CompletedTask);
        await _linker.LinkAsync(false, CancellationToken.None);
        failing = false;

        await _linker.LinkAsync(true, CancellationToken.None);

        Assert.Equal([local.Id], ((Folder)collection).LinkedChildren.Select(child => child.ItemId!.Value));
    }

    [Fact]
    public async Task AFailedGroupDoesNotStopTheOthers()
    {
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 2160);
        var c = Stub("sc", _serverA, 1080);
        var d = Stub("sd", _serverB, 2160);
        await SaveAsync([a, b, c, d], Group("g1", ["sa", "sb"]), Group("g2", ["sc", "sd"]));
        var itemA = Scanned(a);
        Scanned(b);
        var itemC = Scanned(c);
        var itemD = Scanned(d);
        _library.Setup(library => library.UpdateItemAsync(It.Is<BaseItem>(item => item.Id == itemA.Id), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException(TestValues.Host));

        await _linker.LinkAsync(false, CancellationToken.None);

        Assert.Equal(itemD.Id, itemC.PrimaryVersionId);
        Assert.DoesNotContain(_logger.Lines, line => line.Contains(TestValues.Host, StringComparison.Ordinal));
        Assert.Contains(_logger.Lines, line => line.Contains(nameof(IOException), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedSaveIsMadeAgainByTheNextRun()
    {
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 2160);
        await SaveAsync([a, b], Group("g", ["sa", "sb"]));
        var itemA = Scanned(a);
        var itemB = Scanned(b);
        var failing = true;
        _library.Setup(library => library.UpdateItemAsync(It.Is<BaseItem>(item => item.Id == itemA.Id), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Returns(() => failing ? Task.FromException(new IOException()) : Task.CompletedTask)
            .Callback((BaseItem item, BaseItem _, ItemUpdateType reason, CancellationToken _) => _saves.Add((item.Id, reason)));

        await _linker.LinkAsync(false, CancellationToken.None);
        Assert.Null(itemA.PrimaryVersionId);
        failing = false;
        _saves.Clear();
        await _linker.LinkAsync(false, CancellationToken.None);

        Assert.Equal([(itemA.Id, ItemUpdateType.MetadataImport), (itemB.Id, ItemUpdateType.MetadataImport)], _saves);
        Assert.Equal(itemB.Id, itemA.PrimaryVersionId);
    }

    [Fact]
    public async Task AVersionOfALocalFilmIsNeverDetached()
    {
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 2160);
        await SaveAsync([a, b], Group("g", ["sa", "sb"]));
        var itemA = Scanned(a);
        var itemB = Scanned(b);
        var owner = Local();
        var version = Add(new Movie { Path = "/media/films/x - 720p.mkv", OwnerId = owner.Id }, Guid.NewGuid());
        version.SetPrimaryVersionId(itemB.Id);
        itemB.LinkedAlternateVersions = [Link(version.Id)];

        await _linker.LinkAsync(false, CancellationToken.None);

        Assert.Equal(itemB.Id, version.PrimaryVersionId);
        Assert.DoesNotContain(_saves, save => save.Item == version.Id);
        Assert.Equal([itemA.Id], LinkedIds(itemB));
    }

    // Q276: a stub about to be deleted leaves its group first, saved with no metadata saver, while the caller holds the lock.
    [Fact]
    public async Task AStubAboutToBeRemovedLeavesTheLocalCopyWithoutAMetadataSave()
    {
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 1080);
        await SaveAsync([a, b], Group("g", ["sa", "sb"]));
        var itemA = Scanned(a);
        var itemB = Scanned(b);
        var local = Local();
        itemA.SetPrimaryVersionId(local.Id);
        itemB.SetPrimaryVersionId(local.Id);
        local.LinkedAlternateVersions = [Link(itemA.Id), Link(itemB.Id)];

        await _registry.EnterAsync(CancellationToken.None);
        try
        {
            await _linker.DetachForRemovalAsync([a], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            _registry.Exit();
        }

        Assert.Null(itemA.PrimaryVersionId);
        Assert.Equal([itemB.Id], LinkedIds(local));
        Assert.Equal(local.Id, itemB.PrimaryVersionId);
        Assert.Equal(new[] { local.Id, itemA.Id }.Order(), _saves.Select(save => save.Item).Order());
        Assert.All(_saves, save => Assert.Equal(ItemUpdateType.MetadataImport, save.Reason));
    }

    [Fact]
    public async Task APrimaryStubAboutToBeRemovedHandsItsGroupToNoOne()
    {
        var a = Stub("sa", _serverA, 1080);
        var b = Stub("sb", _serverB, 1080);
        await SaveAsync([a, b], Group("g", ["sa", "sb"]));
        var itemA = Scanned(a);
        var itemB = Scanned(b);
        var local = Local();
        local.SetPrimaryVersionId(itemA.Id);
        itemB.SetPrimaryVersionId(itemA.Id);
        itemA.LinkedAlternateVersions = [Link(local.Id), Link(itemB.Id)];

        await _linker.DetachForRemovalAsync([a], CancellationToken.None);

        Assert.Empty(itemA.LinkedAlternateVersions);
        Assert.Null(local.PrimaryVersionId);
        Assert.Null(itemB.PrimaryVersionId);
        Assert.All(_saves, save => Assert.Equal(ItemUpdateType.MetadataImport, save.Reason));
    }

    [Fact]
    public async Task AStubAboutToBeRemovedWithoutLinksIsNotSaved()
    {
        var a = Stub("sa", _serverA, 1080);
        await SaveAsync([a]);
        Scanned(a);

        Assert.Empty(await _linker.DetachForRemovalAsync([a], CancellationToken.None));

        Assert.Empty(_saves);
    }

    [Fact]
    public async Task AFailedDetachBeforeARemovalDoesNotStopTheOthers()
    {
        var a = Stub("sa", _serverA, 1080);
        var c = Stub("sc", _serverA, 720);
        await SaveAsync([a, c]);
        var itemA = Scanned(a);
        var itemC = Scanned(c);
        var local = Local();
        var other = Local();
        itemA.SetPrimaryVersionId(local.Id);
        local.LinkedAlternateVersions = [Link(itemA.Id)];
        itemC.SetPrimaryVersionId(other.Id);
        other.LinkedAlternateVersions = [Link(itemC.Id)];
        _library.Setup(library => library.UpdateItemAsync(It.Is<BaseItem>(item => item.Id == local.Id), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException(TestValues.Host));

        var kept = await _linker.DetachForRemovalAsync([a, c], CancellationToken.None);

        Assert.Equal([a], kept);
        Assert.Null(itemC.PrimaryVersionId);
        Assert.Empty(other.LinkedAlternateVersions);
        Assert.DoesNotContain(_logger.Lines, line => line.Contains(TestValues.Host, StringComparison.Ordinal));
        Assert.Contains(_logger.Lines, line => line.Contains(nameof(IOException), StringComparison.Ordinal));
    }

    [Fact]
    public async Task NothingHappensWithoutAFederatedFolder()
    {
        _store.Configuration.FederatedFolderPath = string.Empty;

        await _linker.LinkAsync(true, CancellationToken.None);

        Assert.Empty(_saves);
    }

    private static Guid IdOf(string key, Type type) => new(MD5.HashData(Encoding.UTF8.GetBytes(type.FullName + key.ToLowerInvariant())));

    private static LinkedChild Link(Guid id) => new() { ItemId = id, Type = LinkedChildType.LinkedAlternateVersion };

    private static Guid[] LinkedIds(Video video) => video.LinkedAlternateVersions.Select(child => child.ItemId!.Value).ToArray();

    private static VersionGroup Group(string id, string[] stubs, Guid[]? local = null, string? primary = null) =>
        new() { Id = id, IdentityKey = id, StubIds = stubs, LocalItemIds = local ?? [], PrimaryStubId = primary };

    private StubEntry Stub(string id, Guid server, int height, StubKind kind = StubKind.Movie)
    {
        var itemId = "item-" + id;
        _catalogs.RemoveAll(catalog => catalog.ServerId == server && catalog.Items.Count == 0);
        var existing = _catalogs.FirstOrDefault(catalog => catalog.ServerId == server);
        var item = new IndexedItem
        {
            ItemId = itemId,
            Kind = kind == StubKind.Movie ? IndexedItemKind.Movie : IndexedItemKind.Episode,
            LibraryId = "lib",
            LastSeenUtc = DateTimeOffset.UnixEpoch,
            Versions = [new IndexedVersion { MediaSourceId = "ms-" + id, Height = height, Codec = "h264", BitRate = 1000, VideoRange = "SDR" }]
        };
        if (existing is null)
        {
            _catalogs.Add(new ServerCatalog { ServerId = server, State = ServerSyncState.Complete, Items = [item] });
        }
        else
        {
            _catalogs[_catalogs.IndexOf(existing)] = existing with { Items = [.. existing.Items, item] };
        }

        return new StubEntry
        {
            Id = id,
            ServerId = server,
            Kind = kind,
            RelativePath = Path.Combine(FederatedPaths.ServerFolder(server), kind == StubKind.Movie ? "Film" : "Serie", id, id + ".strm"),
            Sources = [new StubSource(server, itemId, "ms-" + id)],
            IdentityKey = "key-" + id
        };
    }

    private Video Scanned(StubEntry stub)
    {
        var path = FederatedPaths.Resolve(Root, stub.RelativePath);
        Video video = stub.Kind == StubKind.Movie ? new Movie { Path = path } : new Episode { Path = path };
        return Add(video, IdOf(path, video.GetType()));
    }

    private Video Local(bool played = false)
    {
        var video = Add(new Movie { Path = "/media/films/" + Guid.NewGuid().ToString("N") + ".mkv" }, Guid.NewGuid());
        if (played)
        {
            Data(video).Played = true;
            Data(video).PlayCount = 1;
            Data(video).LastPlayedDate = _older;
        }

        return video;
    }

    private T Add<T>(T item, Guid id)
        where T : BaseItem
    {
        item.Id = id;
        _items[id] = item;
        return item;
    }

    private UserItemData Data(BaseItem item) => _userData.Object.GetUserData(_user, item)!;

    private Task SaveAsync(IReadOnlyList<StubEntry> stubs, params VersionGroup[] groups) =>
        _registry.SaveAsync(StubState.Empty with { Stubs = stubs, Groups = groups }, CancellationToken.None);
}
