using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
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

// The watch state part of the migration and of a server rename (Q41, Q47, D15).
public sealed class WatchStateCarrierTests
{
    private static readonly DateTime _older = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _newer = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid _serverA = Guid.NewGuid();
    private static readonly Guid _serverB = Guid.NewGuid();

    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly InMemoryLastServedStore _lastServed = new();
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly Dictionary<(Guid User, Guid Item), UserItemData> _rows = [];
    private readonly List<(Guid User, Guid Item, UserDataSaveReason Reason)> _saves = [];
    private readonly User _mario = new("mario", "auth", "reset");
    private readonly User _luigi = new("luigi", "auth", "reset");
    private readonly WatchStateCarrier _carrier;

    public WatchStateCarrierTests()
    {
        _library.Setup(library => library.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
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
            .Callback((User user, BaseItem item, UserItemData _, UserDataSaveReason reason, CancellationToken _) => _saves.Add((user.Id, item.Id, reason)));

        _carrier = new WatchStateCarrier(_library.Object, _users.Object, _userData.Object, _lastServed, new CapturingLogger<WatchStateCarrier>());
    }

    [Fact]
    public async Task EachUserGetsThePositionOnTheVersionOfTheServerThatLastServedThem()
    {
        var old = Movie();
        var a = Movie();
        var b = Movie(primary: a);
        Row(_mario, old).PlaybackPositionTicks = 600;
        Row(_mario, old).LastPlayedDate = _newer;
        Row(_luigi, old).PlaybackPositionTicks = 900;
        Row(_luigi, old).LastPlayedDate = _newer;
        await Served(_mario, _serverB);
        await Served(_luigi, _serverA);

        var report = _carrier.Carry([Move(old, [_serverA, _serverB], (a, _serverA), (b, _serverB))], CancellationToken.None);

        Assert.Equal(600, Row(_mario, b).PlaybackPositionTicks);
        Assert.Equal(0, Row(_mario, a).PlaybackPositionTicks);
        Assert.Equal(900, Row(_luigi, a).PlaybackPositionTicks);
        Assert.Equal(0, Row(_luigi, b).PlaybackPositionTicks);
        Assert.Equal(new WatchStateCarryReport(1, 0), report);
        Assert.All(_saves, save => Assert.Equal(UserDataSaveReason.Import, save.Reason));
    }

    [Fact]
    public async Task FavoriteAndPlayCountGoToALocalPrimaryAndPlayedToEveryVersion()
    {
        var old = Movie();
        var local = Movie();
        var a = Movie(primary: local);
        var row = Row(_mario, old);
        row.Played = true;
        row.PlayCount = 2;
        row.LastPlayedDate = _newer;
        row.IsFavorite = true;
        await Served(_mario, _serverA);

        _carrier.Carry([Move(old, [_serverA], (a, _serverA), (local, null))], CancellationToken.None);

        Assert.True(Row(_mario, local).IsFavorite);
        Assert.Equal(2, Row(_mario, local).PlayCount);
        Assert.False(Row(_mario, a).IsFavorite);
        Assert.True(Row(_mario, local).Played);
        Assert.True(Row(_mario, a).Played);
        Assert.DoesNotContain(_rows, entry => entry.Key.User == _luigi.Id && entry.Value.Played);
        Assert.DoesNotContain(_saves, save => save.User == _luigi.Id);
    }

    [Fact]
    public void ASingleNewVersionTakesEverything()
    {
        var old = Movie();
        var renamed = Movie();
        var row = Row(_mario, old);
        row.PlaybackPositionTicks = 300;
        row.PlayCount = 1;
        row.LastPlayedDate = _older;
        row.Rating = 6;

        var report = _carrier.Carry([Move(old, [_serverA], (renamed, _serverA))], CancellationToken.None);

        Assert.Equal(300, Row(_mario, renamed).PlaybackPositionTicks);
        Assert.Equal(1, Row(_mario, renamed).PlayCount);
        Assert.Equal(6, Row(_mario, renamed).Rating);
        Assert.Equal(new WatchStateCarryReport(1, 0), report);
    }

    [Fact]
    public void VersionsNotLinkedYetAreLeftForTheNextRun()
    {
        var old = Movie();
        var a = Movie();
        var b = Movie();
        Row(_mario, old).PlaybackPositionTicks = 300;
        Row(_mario, old).LastPlayedDate = _older;

        var report = _carrier.Carry([Move(old, [_serverA], (a, _serverA), (b, _serverB))], CancellationToken.None);

        Assert.Equal(new WatchStateCarryReport(0, 1), report);
        Assert.Empty(_saves);
    }

    [Fact]
    public void AMissingItemIsLeftForTheNextRun()
    {
        var old = Movie();
        var a = Movie();
        _items.Remove(old.Id);

        var report = _carrier.Carry([Move(old, [_serverA], (a, _serverA))], CancellationToken.None);

        Assert.Equal(new WatchStateCarryReport(0, 1), report);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task ASecondRunSavesNothing()
    {
        var old = Movie();
        var a = Movie();
        var b = Movie(primary: a);
        var row = Row(_mario, old);
        row.Played = true;
        row.PlayCount = 3;
        row.LastPlayedDate = _newer;
        row.IsFavorite = true;
        await Served(_mario, _serverB);
        var move = Move(old, [_serverA, _serverB], (a, _serverA), (b, _serverB));

        _carrier.Carry([move], CancellationToken.None);
        Assert.NotEmpty(_saves);
        _saves.Clear();
        var report = _carrier.Carry([move], CancellationToken.None);

        Assert.Empty(_saves);
        Assert.Equal(new WatchStateCarryReport(1, 0), report);
    }

    [Fact]
    public void TheServersOfAMoveFollowTheSourcesOfTheOldStubBestFirst()
    {
        var stub = new StubEntry
        {
            Id = "old",
            Kind = StubKind.Movie,
            RelativePath = "Film/Old (2020)/Old (2020).strm",
            IdentityKey = "key",
            Sources = [new StubSource(_serverB, "1", "m1"), new StubSource(_serverA, "2", "m2"), new StubSource(_serverB, "3", "m3")]
        };

        var move = WatchStateMove.Of(stub, Guid.NewGuid(), [new WatchStateTarget(Guid.NewGuid(), _serverA)]);

        Assert.Equal([_serverB, _serverA], move.Servers);
        Assert.Equal("old", move.FromStubId);
    }

    private static WatchStateMove Move(BaseItem from, Guid[] servers, params (BaseItem Item, Guid? Server)[] versions) => new()
    {
        FromItemId = from.Id,
        FromStubId = "old",
        Servers = servers,
        Versions = versions.Select(version => new WatchStateTarget(version.Item.Id, version.Server)).ToList()
    };

    private Movie Movie(Movie? primary = null)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Path = "/f/" + Guid.NewGuid().ToString("N") + ".strm" };
        if (primary is not null)
        {
            movie.SetPrimaryVersionId(primary.Id);
            primary.LinkedAlternateVersions = [.. primary.LinkedAlternateVersions, new LinkedChild { ItemId = movie.Id, Type = LinkedChildType.LinkedAlternateVersion }];
        }

        _items[movie.Id] = movie;
        return movie;
    }

    private Task Served(User user, Guid server) => _lastServed.RecordAsync(user.Id, "old", server, CancellationToken.None);

    private UserItemData Row(User user, BaseItem item) => _userData.Object.GetUserData(user, item)!;
}
