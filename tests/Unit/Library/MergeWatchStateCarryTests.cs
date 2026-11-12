using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Tests.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;
using LinkedChildType = MediaBrowser.Controller.Entities.LinkedChildType;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// A manual merge that makes two titles of one library one entity carries the state as Q47, position aside (decision 172).
public sealed class MergeWatchStateCarryTests : IDisposable
{
    private static readonly DateTime _older = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _newer = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid _serverA = Guid.NewGuid();
    private static readonly Guid _serverB = Guid.NewGuid();
    private static readonly Guid _films = Guid.NewGuid();
    private static readonly Guid _otherFilms = Guid.NewGuid();

    private readonly string _data = Path.Combine(Path.GetTempPath(), "jellymesh-merge-carry-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryConfigurationStore _store = new();
    private readonly StubRegistry _registry;
    private readonly WatchStateCarryLog _log;
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly Dictionary<Guid, Guid> _libraryOf = [];
    private readonly Dictionary<(Guid User, Guid Item), UserItemData> _rows = [];
    private readonly List<StubEntry> _stubs = [];
    private readonly List<SeriesStub> _series = [];
    private readonly List<VersionGroup> _groups = [];
    private readonly User _mario = new("mario", "auth", "reset");

    public MergeWatchStateCarryTests()
    {
        _store.Configuration.FederatedFolderPath = Path.Combine(_data, "federata");
        _registry = new StubRegistry(Path.Combine(_data, "registry"), _store, new CapturingLogger<StubRegistry>());
        _log = new WatchStateCarryLog(Path.Combine(_data, "log"), new CapturingLogger<WatchStateCarryLog>());

        _library.Setup(library => library.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
        _library.Setup(library => library.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User>())).Returns((Guid id, User _) => _items.GetValueOrDefault(id));
        _library.Setup(library => library.FindByPath(It.IsAny<string>(), false))
            .Returns((string path, bool? _) => _items.Values.FirstOrDefault(item => item.Path == path));
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>()))
            .Returns((BaseItem item) => _libraryOf.TryGetValue(item.Id, out var folder) ? [new Folder { Id = folder }] : []);
        _users.Setup(users => users.GetUsers()).Returns(() => [_mario]);
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
    public async Task TheTitleThatJoinsGivesFavoriteRatingAndPlayCountToThePrimaryAndKeepsItsPosition()
    {
        var (a, aCopy) = Stub(_serverA, "a1");
        var (b, bCopy) = Stub(_serverB, "b1");
        var row = Row(a);
        row.Played = true;
        row.PlayCount = 2;
        row.PlaybackPositionTicks = 500;
        row.LastPlayedDate = _newer;
        row.IsFavorite = true;
        row.Rating = 7;
        var carry = await SaveAsync();
        var heads = carry.FindHeads([aCopy, bCopy]);

        await LinkAsync(b, a);
        var report = await carry.CarryAsync(heads, CancellationToken.None);

        Assert.Equal(WatchStateMoveStatus.Carried, Assert.Single(report!.Moves).Status);
        Assert.True(Row(b).IsFavorite);
        Assert.Equal(7, Row(b).Rating);
        Assert.Equal(2, Row(b).PlayCount);
        Assert.True(Row(b).Played);
        Assert.Equal(0, Row(b).PlaybackPositionTicks);
        Assert.Equal(500, Row(a).PlaybackPositionTicks);
    }

    [Fact]
    public async Task AStubJoinedWithALocalCopyGivesItsStateToTheLocalCopy()
    {
        var (a, aCopy) = Stub(_serverA, "a1");
        var local = Add(new Movie { Path = "/media/films/local.mkv" }, _films);
        var localCopy = new MergedCopy { ServerId = "local", ItemId = local.Id.ToString("N"), IsLocal = true };
        Row(a).IsFavorite = true;
        Row(a).Played = true;
        Row(a).LastPlayedDate = _older;
        var carry = await SaveAsync();
        var heads = carry.FindHeads([aCopy, localCopy]);

        await LinkAsync(local, a);
        await carry.CarryAsync(heads, CancellationToken.None);

        Assert.True(Row(local).IsFavorite);
        Assert.True(Row(local).Played);
    }

    [Fact]
    public async Task TitlesInDifferentLibrariesStayApartAndCarryNothing()
    {
        var (a, aCopy) = Stub(_serverA, "a1", _otherFilms);
        var (b, bCopy) = Stub(_serverB, "b1");
        Row(a).IsFavorite = true;
        var carry = await SaveAsync();
        var heads = carry.FindHeads([aCopy, bCopy]);

        await LinkAsync(b, a);
        var report = await carry.CarryAsync(heads, CancellationToken.None);

        Assert.Null(report);
        Assert.False(Row(b).IsFavorite);
    }

    [Fact]
    public async Task AStubMergedIntoAnotherOfItsServerGivesItsStateToThePrimaryToo()
    {
        // Its own stub takes its state only after the scan (decision 2); by then the title is one entity already.
        var (a, aCopy) = Stub(_serverA, "a1");
        var (c, cCopy) = Stub(_serverA, "c1");
        var (b, bCopy) = Stub(_serverB, "b1");
        Row(c).IsFavorite = true;
        Row(c).PlayCount = 1;
        Row(c).LastPlayedDate = _older;
        var carry = await SaveAsync();
        var heads = carry.FindHeads([aCopy, cCopy, bCopy]);

        await LinkAsync(b, a);
        var report = await carry.CarryAsync(heads, CancellationToken.None);

        Assert.All(report!.Moves, move => Assert.Equal(WatchStateMoveStatus.Carried, move.Status));
        Assert.True(Row(b).IsFavorite);
        Assert.Equal(1, Row(b).PlayCount);
    }

    [Fact]
    public async Task AVersionThatWasAlreadyAnAlternateGivesNothing()
    {
        var (a, aCopy) = Stub(_serverA, "a1");
        var (b, bCopy) = Stub(_serverB, "b1");
        var (d, dCopy) = Stub(Guid.NewGuid(), "d1");
        await LinkAsync(b, d);
        Row(d).PlayCount = 5;
        Row(d).LastPlayedDate = _newer;
        Row(b).PlayCount = 1;
        Row(b).LastPlayedDate = _older;
        var carry = await SaveAsync();
        var heads = carry.FindHeads([aCopy, bCopy, dCopy]);

        await LinkAsync(b, a, d);
        await carry.CarryAsync(heads, CancellationToken.None);

        Assert.Equal(1, Row(b).PlayCount);
    }

    [Fact]
    public async Task CopiesNotLinkedAsOneTitleCarryNothing()
    {
        var (a, aCopy) = Stub(_serverA, "a1");
        var (b, bCopy) = Stub(_serverB, "b1");
        Row(a).IsFavorite = true;
        var carry = await SaveAsync();
        var heads = carry.FindHeads([aCopy, bCopy]);

        Assert.Null(await carry.CarryAsync(heads, CancellationToken.None));
        Assert.False(Row(b).IsFavorite);
    }

    [Fact]
    public async Task MergedSeriesCarryEpisodeByEpisode_EachGroupToItsPrimary()
    {
        var aCopy = Series(_serverA, "sa");
        var bCopy = Series(_serverB, "sb");
        var a1 = Episode(_serverA, "sa", 1);
        var a2 = Episode(_serverA, "sa", 2);
        var b1 = Episode(_serverB, "sb", 1);
        var b2 = Episode(_serverB, "sb", 2);
        Row(a1).IsFavorite = true;
        Row(a1).PlayCount = 3;
        Row(a2).Rating = 8;
        Row(a2).PlaybackPositionTicks = 900;
        var carry = await SaveAsync();
        var heads = carry.FindHeads([aCopy, bCopy]);

        Assert.Equal(4, heads.Count);
        await LinkAsync(b1, a1);
        await LinkAsync(b2, a2);
        var report = await carry.CarryAsync(heads, CancellationToken.None);

        Assert.Equal(2, report!.Moves.Count);
        Assert.All(report.Moves, move => Assert.Equal(WatchStateMoveStatus.Carried, move.Status));
        Assert.True(Row(b1).IsFavorite);
        Assert.Equal(3, Row(b1).PlayCount);
        Assert.Equal(8, Row(b2).Rating);
        Assert.Equal(0, Row(b2).PlaybackPositionTicks);
        Assert.Equal(900, Row(a2).PlaybackPositionTicks);
    }

    [Fact]
    public async Task AnEpisodeOfAMergedSeriesThatJoinsNoGroupCarriesNothing()
    {
        // Different ranges stay separate episodes (D3), so no two entities became one.
        var aCopy = Series(_serverA, "sa");
        var bCopy = Series(_serverB, "sb");
        var a1 = Episode(_serverA, "sa", 1);
        var a2 = Episode(_serverA, "sa", 2);
        var b1 = Episode(_serverB, "sb", 1);
        var b3 = Episode(_serverB, "sb", 3);
        Row(a2).IsFavorite = true;
        Row(a1).IsFavorite = true;
        var carry = await SaveAsync();
        var heads = carry.FindHeads([aCopy, bCopy]);

        await LinkAsync(b1, a1);
        var report = await carry.CarryAsync(heads, CancellationToken.None);

        Assert.Single(report!.Moves);
        Assert.True(Row(b1).IsFavorite);
        Assert.False(Row(b3).IsFavorite);
    }

    [Fact]
    public async Task EpisodesOfMergedSeriesInDifferentLibrariesCarryNothing()
    {
        var aCopy = Series(_serverA, "sa");
        var bCopy = Series(_serverB, "sb");
        var a1 = Episode(_serverA, "sa", 1, _otherFilms);
        var b1 = Episode(_serverB, "sb", 1);
        Row(a1).IsFavorite = true;
        var carry = await SaveAsync();
        var heads = carry.FindHeads([aCopy, bCopy]);

        await LinkAsync(b1, a1);

        Assert.Null(await carry.CarryAsync(heads, CancellationToken.None));
        Assert.False(Row(b1).IsFavorite);
    }

    [Fact]
    public void ALocalSeriesNamesNoHead_ItsEpisodesAreFoundThroughTheirGroups()
    {
        var local = new MergedCopy { ServerId = "local", ItemId = Guid.NewGuid().ToString("N"), IsLocal = true, Kind = ContentKind.Episode };
        var carry = new MergeWatchStateCarry(_registry, _store, _library.Object, Mock.Of<IWatchStateCarrier>(), new CapturingLogger<MergeWatchStateCarry>());

        Assert.Empty(carry.FindHeads([local]));
    }

    private MergedCopy Series(Guid server, string remoteId)
    {
        var name = Guid.NewGuid().ToString("N");
        _series.Add(new SeriesStub
        {
            Id = remoteId + "-stub",
            ServerId = server,
            RelativePath = Path.Combine(FederatedPaths.ServerFolder(server), "Serie", name),
            WorkKey = "series|" + remoteId,
            Sources = [new StubSource(server, remoteId, string.Empty)]
        });
        return new MergedCopy { ServerId = server.ToString("N"), ItemId = remoteId, Kind = ContentKind.Episode };
    }

    private MediaBrowser.Controller.Entities.TV.Episode Episode(Guid server, string seriesId, int number, Guid? library = null)
    {
        var series = _series.Single(entry => entry.Id == seriesId + "-stub");
        var stub = new StubEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            ServerId = server,
            Kind = StubKind.Episode,
            RelativePath = Path.Combine(series.RelativePath, "Season 01", $"S01E{number:00}.strm"),
            IdentityKey = "key|S1E" + number,
            SeriesStubId = series.Id,
            SeasonNumber = 1,
            EpisodeNumber = number,
            Sources = [new StubSource(server, seriesId + "-e" + number, "ms")]
        };
        _stubs.Add(stub);
        return Add(new MediaBrowser.Controller.Entities.TV.Episode { Path = FederatedPaths.Resolve(Root, stub.RelativePath) }, library ?? _films);
    }

    private (Movie Item, MergedCopy Copy) Stub(Guid server, string remoteId, Guid? library = null)
    {
        var name = Guid.NewGuid().ToString("N");
        var stub = new StubEntry
        {
            Id = name,
            ServerId = server,
            Kind = StubKind.Movie,
            RelativePath = Path.Combine(FederatedPaths.ServerFolder(server), "Film", name, name + ".strm"),
            IdentityKey = "key",
            Sources = [new StubSource(server, remoteId, "ms")]
        };
        _stubs.Add(stub);
        var item = Add(new Movie { Path = FederatedPaths.Resolve(Root, stub.RelativePath) }, library ?? _films);
        return (item, new MergedCopy { ServerId = server.ToString("N"), ItemId = remoteId });
    }

    private T Add<T>(T item, Guid library)
        where T : BaseItem
    {
        item.Id = Guid.NewGuid();
        _items[item.Id] = item;
        _libraryOf[item.Id] = library;
        return item;
    }

    // Links the versions as the linker does and puts them in one registry group.
    private Task LinkAsync(Video primary, params Video[] alternates)
    {
        foreach (var alternate in alternates)
        {
            alternate.SetPrimaryVersionId(primary.Id);
        }

        primary.LinkedAlternateVersions = alternates.Select(alternate => new LinkedChild { ItemId = alternate.Id, Type = LinkedChildType.LinkedAlternateVersion }).ToArray();
        var members = alternates.Prepend(primary).ToList();
        var stubIds = members.Select(member => _stubs.FirstOrDefault(stub => FederatedPaths.Resolve(Root, stub.RelativePath) == member.Path)?.Id).OfType<string>().ToHashSet();
        _groups.RemoveAll(group => group.StubIds.Any(stubIds.Contains));
        _groups.Add(new VersionGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            IdentityKey = "key",
            StubIds = stubIds.ToList(),
            LocalItemIds = members.Where(member => !member.Path.StartsWith(Root, StringComparison.Ordinal)).Select(member => member.Id).ToList()
        });
        return SaveStateAsync();
    }

    private Task SaveStateAsync() =>
        _registry.SaveAsync(StubState.Empty with { Stubs = _stubs.ToList(), Series = _series.ToList(), Groups = _groups.ToList() }, CancellationToken.None);

    private async Task<MergeWatchStateCarry> SaveAsync()
    {
        await SaveStateAsync();
        var carrier = new WatchStateCarrier(_registry, _store, _library.Object, _users.Object, _userData.Object, new InMemoryLastServedStore(), _log, new CapturingLogger<WatchStateCarrier>());
        return new MergeWatchStateCarry(_registry, _store, _library.Object, carrier, new CapturingLogger<MergeWatchStateCarry>());
    }

    private UserItemData Row(BaseItem item) => _userData.Object.GetUserData(_mario, item)!;
}
