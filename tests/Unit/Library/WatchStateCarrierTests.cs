using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Library;
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

// The watch state part of the migration and of a server rename (Q41, Q47, D15, decisions 149-155).
public sealed class WatchStateCarrierTests : IDisposable
{
    private static readonly DateTime _older = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _newer = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _newest = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid _serverA = Guid.NewGuid();
    private static readonly Guid _serverB = Guid.NewGuid();

    private readonly string _data = Path.Combine(Path.GetTempPath(), "jellymesh-carrier-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryConfigurationStore _store = new();
    private readonly StubRegistry _registry;
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly InMemoryLastServedStore _lastServed = new();
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly HashSet<(Guid User, Guid Item)> _hidden = [];
    private readonly HashSet<Guid> _failingSaves = [];
    private readonly Dictionary<(Guid User, Guid Item), UserItemData> _rows = [];
    private readonly List<(Guid User, Guid Item, UserDataSaveReason Reason)> _saves = [];
    private readonly List<StubEntry> _stubs = [];
    private readonly List<SeriesStub> _series = [];
    private readonly List<VersionGroup> _groups = [];
    private readonly User _mario = new("mario", "auth", "reset");
    private readonly User _luigi = new("luigi", "auth", "reset");
    private WatchStateCarryLog _log;
    private string? _lastServedStub;

    public WatchStateCarrierTests()
    {
        _store.Configuration.FederatedFolderPath = Path.Combine(_data, "federata");
        _registry = new StubRegistry(Path.Combine(_data, "registry"), _store, new CapturingLogger<StubRegistry>());
        _log = new WatchStateCarryLog(Path.Combine(_data, "log"), new CapturingLogger<WatchStateCarryLog>());

        _library.Setup(library => library.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
        _library.Setup(library => library.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User>()))
            .Returns((Guid id, User user) => _hidden.Contains((user.Id, id)) ? null : _items.GetValueOrDefault(id));
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
            .Callback((User user, BaseItem item, UserItemData data, UserDataSaveReason reason, CancellationToken _) =>
            {
                if (_failingSaves.Contains(item.Id))
                {
                    throw new IOException("disk full");
                }

                // Like Jellyfin, the saved row replaces the cached one.
                _rows[(user.Id, item.Id)] = data;
                _saves.Add((user.Id, item.Id, reason));
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
    public async Task EachUserGetsThePositionOnTheVersionOfTheServerThatLastServedThem()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var b = Movie(_serverB, primary: a);
        Group(a, b);
        Row(_mario, old).PlaybackPositionTicks = 600;
        Row(_mario, old).LastPlayedDate = _newer;
        Row(_luigi, old).PlaybackPositionTicks = 900;
        Row(_luigi, old).LastPlayedDate = _newer;
        await Served(_mario, _serverB);
        await Served(_luigi, _serverA);

        var report = await CarryAsync(Move(old, oldStub, (a, _serverA), (b, _serverB)));

        Assert.Equal(600, Row(_mario, b).PlaybackPositionTicks);
        Assert.Equal(0, Row(_mario, a).PlaybackPositionTicks);
        Assert.Equal(900, Row(_luigi, a).PlaybackPositionTicks);
        Assert.Equal(0, Row(_luigi, b).PlaybackPositionTicks);
        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.Equal((1, 0), (report.Carried, report.Skipped));
        Assert.All(_saves, save => Assert.Equal(UserDataSaveReason.Import, save.Reason));
    }

    [Fact]
    public async Task FavoriteAndPlayCountGoToALocalPrimaryAndPlayedToEveryVersion()
    {
        var (old, oldStub) = Old();
        var local = Local();
        var a = Movie(_serverA, primary: local);
        Group(a, local);
        var row = Row(_mario, old);
        row.Played = true;
        row.PlayCount = 2;
        row.LastPlayedDate = _newer;
        row.IsFavorite = true;
        await Served(_mario, _serverA);

        await CarryAsync(Move(old, oldStub, (a, _serverA), (local, null)));

        Assert.True(Row(_mario, local).IsFavorite);
        Assert.Equal(2, Row(_mario, local).PlayCount);
        Assert.False(Row(_mario, a).IsFavorite);
        Assert.True(Row(_mario, local).Played);
        Assert.True(Row(_mario, a).Played);
        Assert.DoesNotContain(_rows, entry => entry.Key.User == _luigi.Id && entry.Value.Played);
        Assert.DoesNotContain(_saves, save => save.User == _luigi.Id);
    }

    [Fact]
    public async Task AUserWhoCannotSeeAServersLibraryGetsTheStateOnTheVersionsTheySee()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var b = Movie(_serverB, primary: a);
        Group(a, b);
        foreach (var user in new[] { _mario, _luigi })
        {
            var row = Row(user, old);
            row.Played = true;
            row.PlayCount = 2;
            row.PlaybackPositionTicks = 450;
            row.LastPlayedDate = _newer;
            row.IsFavorite = true;
            row.Rating = 8;
            await Served(user, _serverA);
        }

        _hidden.Add((_luigi.Id, a.Id));

        var report = await CarryAsync(Move(old, oldStub, (a, _serverA), (b, _serverB)));

        Assert.True(Row(_mario, a).IsFavorite);
        Assert.Equal(2, Row(_mario, a).PlayCount);
        Assert.False(Row(_mario, b).IsFavorite);
        Assert.False(Row(_luigi, a).IsFavorite);
        Assert.Null(Row(_luigi, a).Rating);
        Assert.Equal(0, Row(_luigi, a).PlayCount);
        Assert.True(Row(_luigi, b).IsFavorite);
        Assert.Equal(8, Row(_luigi, b).Rating);
        Assert.Equal(2, Row(_luigi, b).PlayCount);
        Assert.Equal(450, Row(_mario, a).PlaybackPositionTicks);
        Assert.Equal(450, Row(_luigi, b).PlaybackPositionTicks);
        Assert.Equal(0, Row(_luigi, a).PlaybackPositionTicks);
        Assert.True(Row(_luigi, a).Played);
        Assert.True(Row(_luigi, b).Played);
        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
    }

    [Fact]
    public async Task AUserWhoSeesNoVersionIsReportedAndCarriedOnceTheySeeOne()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        Row(_mario, old).PlaybackPositionTicks = 300;
        Row(_mario, old).LastPlayedDate = _older;
        Row(_luigi, old).PlaybackPositionTicks = 500;
        Row(_luigi, old).LastPlayedDate = _older;
        _hidden.Add((_luigi.Id, a.Id));
        var move = Move(old, oldStub, (a, _serverA));

        var report = await CarryAsync(move);

        var outcome = report.Moves.Single();
        Assert.Equal(WatchStateMoveStatus.UsersLeft, outcome.Status);
        Assert.Equal([new WatchStateUserSkip(_luigi.Id, WatchStateUserSkipReason.NoVisibleVersion)], outcome.SkippedUsers);
        Assert.Equal((0, 1), (report.Carried, report.Skipped));
        Assert.Equal(300, Row(_mario, a).PlaybackPositionTicks);
        Assert.Equal(0, Row(_luigi, a).PlaybackPositionTicks);
        Assert.DoesNotContain(_saves, save => save.User == _luigi.Id);

        _hidden.Clear();
        report = await CarryAsync(move);

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.Equal(500, Row(_luigi, a).PlaybackPositionTicks);
    }

    [Fact]
    public async Task ASingleNewVersionTakesEverything()
    {
        var (old, oldStub) = Old();
        var renamed = Movie(_serverA);
        var row = Row(_mario, old);
        row.PlaybackPositionTicks = 300;
        row.PlayCount = 1;
        row.LastPlayedDate = _older;
        row.Rating = 6;

        var report = await CarryAsync(Move(old, oldStub, (renamed, _serverA)));

        Assert.Equal(300, Row(_mario, renamed).PlaybackPositionTicks);
        Assert.Equal(1, Row(_mario, renamed).PlayCount);
        Assert.Equal(6, Row(_mario, renamed).Rating);
        Assert.Equal((1, 0), (report.Carried, report.Skipped));
    }

    [Fact]
    public async Task VersionsNotLinkedYetAreLeftForTheNextRun()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var b = Movie(_serverB);
        Group(a, b);
        Row(_mario, old).PlaybackPositionTicks = 300;
        Row(_mario, old).LastPlayedDate = _older;

        var report = await CarryAsync(Move(old, oldStub, (a, _serverA), (b, _serverB)));

        Assert.Equal(WatchStateMoveStatus.NotLinked, report.Moves.Single().Status);
        Assert.Equal((0, 1), (report.Carried, report.Skipped));
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task AVersionMissingFromTheLinksOfThePrimaryIsNotLinked()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var b = Movie(_serverB);
        b.SetPrimaryVersionId(a.Id);
        Group(a, b);
        Row(_mario, old).PlaybackPositionTicks = 300;
        Row(_mario, old).LastPlayedDate = _older;

        var report = await CarryAsync(Move(old, oldStub, (a, _serverA), (b, _serverB)));

        Assert.Equal(WatchStateMoveStatus.NotLinked, report.Moves.Single().Status);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task AMissingItemIsLeftForTheNextRun()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        _items.Remove(old.Id);

        var report = await CarryAsync(Move(old, oldStub, (a, _serverA)));

        Assert.Equal(WatchStateMoveStatus.NotFound, report.Moves.Single().Status);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task AnOldItemThatIsNotTheOldStubIsRefused()
    {
        var (old, oldStub) = Old();
        var other = Add(new Movie { Path = "/media/films/other.mkv" });
        var a = Movie(_serverA);
        Row(_mario, other).PlaybackPositionTicks = 300;
        Row(_mario, other).LastPlayedDate = _older;

        var report = await CarryAsync(WatchStateMove.Of(oldStub, other.Id, [new WatchStateTarget(a.Id, _serverA)]));

        Assert.Equal(WatchStateMoveStatus.Refused, report.Moves.Single().Status);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task ATargetThatIsNotAStubOfItsServerIsRefused()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var loose = Add(new Movie { Path = Path.Combine(Root, "loose.strm") });
        Row(_mario, old).PlaybackPositionTicks = 300;
        Row(_mario, old).LastPlayedDate = _older;

        Assert.Equal(WatchStateMoveStatus.Refused, (await CarryAsync(Move(old, oldStub, (a, _serverB)))).Moves.Single().Status);
        Assert.Equal(WatchStateMoveStatus.Refused, (await CarryAsync(Move(old, oldStub, (loose, _serverA)))).Moves.Single().Status);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task TwoLocalTargetsOrALocalOneThatIsNotThePrimaryAreRefused()
    {
        var (old, oldStub) = Old();
        var local = Local();
        var other = Local();
        var a = Movie(_serverA, primary: local);
        Group(a, local);
        var b = Movie(_serverB);
        var secondary = Local();
        Link(b, secondary);
        Group(b, secondary);
        Row(_mario, old).PlaybackPositionTicks = 300;
        Row(_mario, old).LastPlayedDate = _older;

        Assert.Equal(WatchStateMoveStatus.Refused, (await CarryAsync(Move(old, oldStub, (a, _serverA), (local, null), (other, null)))).Moves.Single().Status);
        Assert.Equal(WatchStateMoveStatus.Refused, (await CarryAsync(Move(old, oldStub, (b, _serverB), (secondary, null)))).Moves.Single().Status);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task ARepeatedTargetCountsOnce()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        Row(_mario, old).PlayCount = 1;
        Row(_mario, old).LastPlayedDate = _older;

        var report = await CarryAsync(Move(old, oldStub, (a, _serverA), (a, _serverA)));

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.Equal(1, Row(_mario, a).PlayCount);
    }

    [Fact]
    public async Task EpisodesNeverLinkedTakeTheStateEachOnItsOwnWhereTheUserSeesThem()
    {
        var (old, oldStub) = Old(StubKind.Episode);
        var joined = Episode(_serverA);
        var first = Episode(_serverB);
        var second = Episode(_serverB);
        var row = Row(_mario, old);
        row.PlaybackPositionTicks = 700;
        row.PlayCount = 1;
        row.LastPlayedDate = _newer;
        row.IsFavorite = true;
        _hidden.Add((_mario.Id, second.Id));

        var report = await CarryAsync(Move(old, oldStub, (joined, _serverA), (first, _serverB), (second, _serverB)));

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.All([joined, first], episode => Assert.Equal(700, Row(_mario, episode).PlaybackPositionTicks));
        Assert.All([joined, first], episode => Assert.True(Row(_mario, episode).IsFavorite));
        Assert.All([joined, first], episode => Assert.Equal(1, Row(_mario, episode).PlayCount));
        Assert.Equal(0, Row(_mario, second).PlaybackPositionTicks);
        Assert.False(Row(_mario, second).IsFavorite);
        Assert.DoesNotContain(_saves, save => save.Item == second.Id);
    }

    [Fact]
    public async Task ASeriesFavoriteAndRatingGoToEveryNewSeriesTheUserSees()
    {
        var (old, oldStub) = OldSeries();
        var a = Series(_serverA);
        var b = Series(_serverB);
        foreach (var user in new[] { _mario, _luigi })
        {
            Row(user, old).IsFavorite = true;
            Row(user, old).Rating = 9;
        }

        _hidden.Add((_luigi.Id, b.Id));

        var report = await CarryAsync(WatchStateMove.OfSeries(oldStub, old.Id, [new WatchStateTarget(a.Id, _serverA), new WatchStateTarget(b.Id, _serverB)]));

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.All([a, b], series => Assert.True(Row(_mario, series).IsFavorite));
        Assert.All([a, b], series => Assert.Equal(9, Row(_mario, series).Rating));
        Assert.True(Row(_luigi, a).IsFavorite);
        Assert.False(Row(_luigi, b).IsFavorite);
        Assert.Null(Row(_luigi, b).Rating);
    }

    [Fact]
    public async Task ASeriesFavoriteTakenOffIsNotPutBack()
    {
        var (old, oldStub) = OldSeries();
        var a = Series(_serverA);
        Row(_mario, old).IsFavorite = true;
        var move = WatchStateMove.OfSeries(oldStub, old.Id, [new WatchStateTarget(a.Id, _serverA)]);
        await CarryAsync(move);
        Assert.True(Row(_mario, a).IsFavorite);

        Row(_mario, a).IsFavorite = false;
        _saves.Clear();
        await RestartAsync();
        var report = await CarryAsync(move);

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.Empty(_saves);
        Assert.False(Row(_mario, a).IsFavorite);
    }

    [Fact]
    public async Task ASeriesMoveToALocalSeriesIsRefused()
    {
        var (old, oldStub) = OldSeries();
        var a = Series(_serverA);
        var local = Add(new Series { Path = "/media/serie/local" });
        Row(_mario, old).IsFavorite = true;

        var report = await CarryAsync(WatchStateMove.OfSeries(oldStub, old.Id, [new WatchStateTarget(a.Id, _serverA), new WatchStateTarget(local.Id, null)]));

        Assert.Equal(WatchStateMoveStatus.Refused, report.Moves.Single().Status);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task ASecondRunSavesNothing()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var b = Movie(_serverB, primary: a);
        Group(a, b);
        var row = Row(_mario, old);
        row.Played = true;
        row.PlayCount = 3;
        row.LastPlayedDate = _newer;
        row.IsFavorite = true;
        await Served(_mario, _serverB);
        var move = Move(old, oldStub, (a, _serverA), (b, _serverB));

        await CarryAsync(move);
        Assert.NotEmpty(_saves);
        _saves.Clear();
        var report = await CarryAsync(move);

        Assert.Empty(_saves);
        Assert.Equal((1, 0), (report.Carried, report.Skipped));
    }

    [Fact]
    public async Task ALaterRunDoesNotPutBackWhatTheUserTookOff()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var row = Row(_mario, old);
        row.Played = true;
        row.PlayCount = 1;
        row.LastPlayedDate = _older;
        row.IsFavorite = true;
        row.Rating = 7;
        var move = Move(old, oldStub, (a, _serverA));
        await CarryAsync(move);

        Row(_mario, a).IsFavorite = false;
        Row(_mario, a).Rating = null;
        Row(_mario, a).Played = false;
        _saves.Clear();
        await RestartAsync();
        await CarryAsync(move);

        Assert.Empty(_saves);
        Assert.False(Row(_mario, a).IsFavorite);
        Assert.False(Row(_mario, a).Played);
    }

    [Fact]
    public async Task AWatchOnTheOldItemAfterTheCarryIsCarriedWithoutFavoriteOrRating()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var row = Row(_mario, old);
        row.PlaybackPositionTicks = 300;
        row.LastPlayedDate = _older;
        row.IsFavorite = true;
        var move = Move(old, oldStub, (a, _serverA));
        await CarryAsync(move);
        Row(_mario, a).IsFavorite = false;

        // The manual migration leaves the old stubs playable (D12).
        Row(_mario, old).PlaybackPositionTicks = 0;
        Row(_mario, old).Played = true;
        Row(_mario, old).PlayCount = 1;
        Row(_mario, old).LastPlayedDate = _newest;
        Row(_mario, old).Rating = 5;
        await CarryAsync(move);

        Assert.Equal(0, Row(_mario, a).PlaybackPositionTicks);
        Assert.True(Row(_mario, a).Played);
        Assert.Equal(1, Row(_mario, a).PlayCount);
        Assert.Equal(_newest, Row(_mario, a).LastPlayedDate);
        Assert.False(Row(_mario, a).IsFavorite);
        Assert.Null(Row(_mario, a).Rating);
    }

    [Fact]
    public async Task AFailedSaveLeavesTheCachedRowsAsTheyWereAndTheUserForTheNextRun()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var b = Movie(_serverB, primary: a);
        Group(a, b);
        var row = Row(_mario, old);
        row.Played = true;
        row.PlayCount = 2;
        row.LastPlayedDate = _newer;
        row.IsFavorite = true;
        var cachedA = Row(_mario, a);
        var cachedB = Row(_mario, b);
        _failingSaves.Add(b.Id);
        var move = Move(old, oldStub, (a, _serverA), (b, _serverB));

        var report = await CarryAsync(move);

        var outcome = report.Moves.Single();
        Assert.Equal(WatchStateMoveStatus.UsersLeft, outcome.Status);
        Assert.Contains(new WatchStateUserSkip(_mario.Id, WatchStateUserSkipReason.NotSaved), outcome.SkippedUsers);
        Assert.False(cachedA.IsFavorite);
        Assert.False(cachedB.Played);
        Assert.Same(cachedB, Row(_mario, b));

        _failingSaves.Clear();
        report = await CarryAsync(move);

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.True(Row(_mario, b).Played);
        Assert.True(Row(_mario, a).IsFavorite);
    }

    [Fact]
    public async Task ACarryWhoseRecordCannotBeSavedIsReportedAsFailed()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;
        _log.Dispose();
        Directory.CreateDirectory(_data);
        await File.WriteAllTextAsync(Path.Combine(_data, "log"), "in the way");
        _log = new WatchStateCarryLog(Path.Combine(_data, "log"), new CapturingLogger<WatchStateCarryLog>());

        var move = Move(old, oldStub, (a, _serverA));

        var report = await CarryAsync(move);

        Assert.Equal(WatchStateMoveStatus.Failed, report.Moves.Single().Status);
        Assert.Equal([new WatchStateUserSkip(_mario.Id, WatchStateUserSkipReason.NotRecorded)], report.Moves.Single().SkippedUsers);
        Assert.Equal((0, 1), (report.Carried, report.Skipped));

        // The record kept in memory does not make the next run look done while it is still not on disk.
        report = await CarryAsync(move);

        Assert.Equal(WatchStateMoveStatus.Failed, report.Moves.Single().Status);

        File.Delete(Path.Combine(_data, "log"));
        report = await CarryAsync(move);

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.True(File.Exists(Path.Combine(_data, "log", WatchStateCarryLog.FileName)));
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
        Assert.Equal(stub.RelativePath, move.FromPath);
    }

    private static WatchStateMove Move(BaseItem from, StubEntry stub, params (BaseItem Item, Guid? Server)[] versions) =>
        WatchStateMove.Of(stub, from.Id, versions.Select(version => new WatchStateTarget(version.Item.Id, version.Server)).ToList());

    private static void Link(Video primary, Video alternate)
    {
        alternate.SetPrimaryVersionId(primary.Id);
        primary.LinkedAlternateVersions = [.. primary.LinkedAlternateVersions, new LinkedChild { ItemId = alternate.Id, Type = LinkedChildType.LinkedAlternateVersion }];
    }

    private async Task<WatchStateCarryReport> CarryAsync(params WatchStateMove[] moves)
    {
        await _registry.SaveAsync(StubState.Empty with { Stubs = _stubs.ToList(), Series = _series.ToList(), Groups = _groups.ToList() }, CancellationToken.None);
        var carrier = new WatchStateCarrier(_registry, _store, _library.Object, _users.Object, _userData.Object, _lastServed, _log, new CapturingLogger<WatchStateCarrier>());
        return await carrier.CarryAsync(moves, CancellationToken.None);
    }

    private async Task RestartAsync()
    {
        await _log.SaveAsync(CancellationToken.None);
        _log.Dispose();
        _log = new WatchStateCarryLog(Path.Combine(_data, "log"), new CapturingLogger<WatchStateCarryLog>());
    }

    // The old flat stub and its item.
    private (Video Item, StubEntry Stub) Old(StubKind kind = StubKind.Movie)
    {
        var name = "old-" + Guid.NewGuid().ToString("N");
        var stub = new StubEntry
        {
            Id = name,
            Kind = kind,
            RelativePath = Path.Combine(kind == StubKind.Movie ? "Film" : "Serie", name, name + ".strm"),
            IdentityKey = "key",
            Sources = [new StubSource(_serverA, "1", "m1"), new StubSource(_serverB, "2", "m2")]
        };
        var path = FederatedPaths.Resolve(Root, stub.RelativePath);
        Video item = kind == StubKind.Movie ? new Movie { Path = path } : new Episode { Path = path };
        _lastServedStub = stub.Id;
        return (Add(item), stub);
    }

    private (Series Item, SeriesStub Stub) OldSeries()
    {
        var name = "old-" + Guid.NewGuid().ToString("N");
        var stub = new SeriesStub { Id = name, RelativePath = Path.Combine("Serie", name), WorkKey = "key" };
        return (Add(new Series { Path = FederatedPaths.Resolve(Root, stub.RelativePath) }), stub);
    }

    private Movie Movie(Guid server, Movie? primary = null)
    {
        var movie = (Movie)NewStub(server, StubKind.Movie);
        if (primary is not null)
        {
            Link(primary, movie);
        }

        return movie;
    }

    private Episode Episode(Guid server) => (Episode)NewStub(server, StubKind.Episode);

    private Video NewStub(Guid server, StubKind kind)
    {
        var name = Guid.NewGuid().ToString("N");
        var stub = new StubEntry
        {
            Id = name,
            ServerId = server,
            Kind = kind,
            RelativePath = Path.Combine(FederatedPaths.ServerFolder(server), kind == StubKind.Movie ? "Film" : "Serie", name, name + ".strm"),
            IdentityKey = "key",
            Sources = [new StubSource(server, name, "ms")]
        };
        _stubs.Add(stub);
        var path = FederatedPaths.Resolve(Root, stub.RelativePath);
        return Add<Video>(kind == StubKind.Movie ? new Movie { Path = path } : new Episode { Path = path });
    }

    private Series Series(Guid server)
    {
        var name = Guid.NewGuid().ToString("N");
        var stub = new SeriesStub { Id = name, ServerId = server, RelativePath = FederatedPaths.SeriesFolderOf(server, name), WorkKey = "key" };
        _series.Add(stub);
        return Add(new Series { Path = FederatedPaths.Resolve(Root, stub.RelativePath) });
    }

    private Movie Local() => Add(new Movie { Path = "/media/films/" + Guid.NewGuid().ToString("N") + ".mkv" });

    // A group of the registry with these stubs and local copies.
    private void Group(params Video[] members)
    {
        var stubs = members.Select(member => _stubs.FirstOrDefault(stub => FederatedPaths.Resolve(Root, stub.RelativePath) == member.Path)).ToList();
        _groups.Add(new VersionGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            IdentityKey = "key",
            StubIds = stubs.OfType<StubEntry>().Select(stub => stub.Id).ToList(),
            LocalItemIds = members.Where((_, index) => stubs[index] is null).Select(member => member.Id).ToList()
        });
    }

    private T Add<T>(T item)
        where T : BaseItem
    {
        item.Id = Guid.NewGuid();
        _items[item.Id] = item;
        return item;
    }

    private Task Served(User user, Guid server) => _lastServed.RecordAsync(user.Id, _lastServedStub!, server, CancellationToken.None);

    private UserItemData Row(User user, BaseItem item) => _userData.Object.GetUserData(user, item)!;
}
