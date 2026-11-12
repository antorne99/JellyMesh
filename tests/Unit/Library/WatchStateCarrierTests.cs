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
    private readonly HashSet<Guid> _cancellingSaves = [];
    private readonly HashSet<Guid> _failingLookups = [];
    private readonly CapturingLogger<WatchStateCarrier> _carrierLogger = new();
    private readonly CancellationTokenSource _cancellation = new();
    private Guid? _cancelOnSave;
    private readonly Dictionary<(Guid User, Guid Item), UserItemData> _rows = [];
    private readonly List<(Guid User, Guid Item, UserDataSaveReason Reason)> _saves = [];
    private readonly List<StubEntry> _stubs = [];
    private readonly List<SeriesStub> _series = [];
    private readonly List<VersionGroup> _groups = [];
    private readonly User _mario = new("mario", "auth", "reset");
    private readonly User _luigi = new("luigi", "auth", "reset");
    private readonly List<User> _everyone;
    private WatchStateCarryLog _log;
    private string? _lastServedStub;

    public WatchStateCarrierTests()
    {
        _store.Configuration.FederatedFolderPath = Path.Combine(_data, "federata");
        _registry = new StubRegistry(Path.Combine(_data, "registry"), _store, new CapturingLogger<StubRegistry>());
        _log = new WatchStateCarryLog(Path.Combine(_data, "log"), new CapturingLogger<WatchStateCarryLog>());

        _library.Setup(library => library.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _failingLookups.Contains(id) ? throw new IOException("/secret") : _items.GetValueOrDefault(id));
        _library.Setup(library => library.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User>()))
            .Returns((Guid id, User user) => _hidden.Contains((user.Id, id)) ? null : _items.GetValueOrDefault(id));
        _everyone = [_mario, _luigi];
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
                    throw new IOException("disk full");
                }

                if (_cancellingSaves.Contains(item.Id))
                {
                    throw new OperationCanceledException();
                }

                // Like Jellyfin, the saved row replaces the cached one.
                _rows[(user.Id, item.Id)] = data;
                _saves.Add((user.Id, item.Id, reason));
                if (_cancelOnSave == item.Id)
                {
                    _cancellation.Cancel();
                }
            });
    }

    private string Root => _store.Configuration.FederatedFolderPath;

    public void Dispose()
    {
        _registry.Dispose();
        _log.Dispose();
        _cancellation.Dispose();
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
    public async Task TheNewStubOfARenamedServerOwnedByTheOldItemTakesEverything()
    {
        var (old, oldStub) = Old();
        var renamed = Movie(_serverA);
        renamed.OwnerId = old.Id;
        renamed.SetPrimaryVersionId(old.Id);
        var row = Row(_mario, old);
        row.PlaybackPositionTicks = 300;
        row.PlayCount = 2;
        row.LastPlayedDate = _older;

        var report = await CarryAsync(RemovingMove(old, oldStub, (renamed, _serverA)));

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.Equal(300, Row(_mario, renamed).PlaybackPositionTicks);
        Assert.Equal(2, Row(_mario, renamed).PlayCount);
        Assert.Equal([old.Id], report.ForgettableOldItemIds);
    }

    [Fact]
    public async Task AVersionOwnedByTheOldItemIsNotLinkedWhenTheOldItemStays()
    {
        var (old, oldStub) = Old();
        var renamed = Movie(_serverA);
        renamed.OwnerId = old.Id;
        renamed.SetPrimaryVersionId(old.Id);
        Row(_mario, old).PlaybackPositionTicks = 300;
        Row(_mario, old).LastPlayedDate = _older;

        var report = await CarryAsync(Move(old, oldStub, (renamed, _serverA)));

        Assert.Equal(WatchStateMoveStatus.NotLinked, report.Moves.Single().Status);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task AVersionOwnedByTheOldItemInATitleOfTwoIsNotLinked()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var b = Movie(_serverB);
        a.OwnerId = old.Id;
        a.SetPrimaryVersionId(old.Id);
        Link(a, b);
        Group(a, b);
        Row(_mario, old).PlaybackPositionTicks = 300;
        Row(_mario, old).LastPlayedDate = _older;

        var report = await CarryAsync(RemovingMove(old, oldStub, (a, _serverA), (b, _serverB)));

        Assert.Equal(WatchStateMoveStatus.NotLinked, report.Moves.Single().Status);
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
    public async Task TheRecordsOfAnOldItemNoLongerInTheLibraryGoAtTheEndOfARun()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var row = Row(_mario, old);
        row.IsFavorite = true;
        row.LastPlayedDate = _older;
        var move = Move(old, oldStub, (a, _serverA));
        await CarryAsync(move);
        Assert.True(_log.TryFind(_mario.Id, old.Id, out _));

        _items.Remove(old.Id);
        await CarryAsync();
        await RestartAsync();

        Assert.False(_log.TryFind(_mario.Id, old.Id, out _));
    }

    [Fact]
    public async Task ARunWhoseCleanUpFailsStillSavesItsRecords()
    {
        var (first, firstStub) = Old();
        var a = Movie(_serverA);
        Row(_mario, first).IsFavorite = true;
        await CarryAsync(Move(first, firstStub, (a, _serverA)));
        var (second, secondStub) = Old();
        var b = Movie(_serverA);
        Row(_mario, second).IsFavorite = true;
        _failingLookups.Add(first.Id);

        var report = await CarryAsync(Move(second, secondStub, (b, _serverA)));

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.Contains(_carrierLogger.Lines, line => line.Contains(nameof(IOException), StringComparison.Ordinal));
        Assert.DoesNotContain(_carrierLogger.Lines, line => line.Contains("secret", StringComparison.Ordinal));
        _log.Dispose();
        _log = new WatchStateCarryLog(Path.Combine(_data, "log"), new CapturingLogger<WatchStateCarryLog>());
        Assert.True(_log.TryFind(_mario.Id, first.Id, out _));
        Assert.True(_log.TryFind(_mario.Id, second.Id, out _));
    }

    [Fact]
    public async Task APathThatComesBackCarriesItsStateAgain()
    {
        // A to B, then B to A, then A to B again: the old item of the last move had a record from the first (decision 165).
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;
        await CarryAsync(Move(old, oldStub, (a, _serverA)));
        _items.Remove(old.Id);
        await CarryAsync();

        _items[old.Id] = old;
        var b = Movie(_serverA);
        Row(_mario, old).Rating = 7;
        var report = await CarryAsync(Move(old, oldStub, (b, _serverA)));

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.True(Row(_mario, b).IsFavorite);
        Assert.Equal(7, Row(_mario, b).Rating);
    }

    [Fact]
    public async Task TheRecordsOfAUserWhoIsGoneGoAtTheEndOfARun()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;
        Row(_luigi, old).IsFavorite = true;
        await CarryAsync(Move(old, oldStub, (a, _serverA)));
        Assert.True(_log.TryFind(_luigi.Id, old.Id, out _));

        _everyone.Remove(_luigi);
        await CarryAsync();
        await RestartAsync();

        Assert.False(_log.TryFind(_luigi.Id, old.Id, out _));
        Assert.True(_log.TryFind(_mario.Id, old.Id, out _));
    }

    [Fact]
    public async Task ALogThatCannotBeReadForNowSuspendsEveryCarry()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;
        await CarryAsync(Move(old, oldStub, (a, _serverA)));
        Row(_mario, a).IsFavorite = false;
        _saves.Clear();
        var folder = Path.Combine(_data, "log");
        var file = Path.Combine(folder, WatchStateCarryLog.FileName);
        var content = await File.ReadAllTextAsync(file);
        _log.Dispose();
        var failing = true;
        _log = new WatchStateCarryLog(folder, new CapturingLogger<WatchStateCarryLog>(), path => failing ? throw new IOException("busy") : File.OpenRead(path));

        var report = await CarryAsync(Move(old, oldStub, (a, _serverA)));

        Assert.Equal(WatchStateMoveStatus.Failed, report.Moves.Single().Status);
        Assert.Empty(_saves);
        Assert.Equal(content, await File.ReadAllTextAsync(file));

        failing = false;
        report = await CarryAsync(Move(old, oldStub, (a, _serverA)));

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.False(Row(_mario, a).IsFavorite);
    }

    [Fact]
    public async Task AFavoriteSavedBeforeAnotherRowFailedIsNotPutBackByTheNextRun()
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
        row.Rating = 6;
        _failingSaves.Add(b.Id);
        var move = Move(old, oldStub, (a, _serverA), (b, _serverB));

        var report = await CarryAsync(move);

        Assert.Equal(WatchStateMoveStatus.UsersLeft, report.Moves.Single().Status);
        Assert.True(Row(_mario, a).IsFavorite);

        Row(_mario, a).IsFavorite = false;
        Row(_mario, a).Rating = null;
        _failingSaves.Clear();
        await RestartAsync();
        report = await CarryAsync(move);

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.True(Row(_mario, b).Played);
        Assert.False(Row(_mario, a).IsFavorite);
        Assert.Null(Row(_mario, a).Rating);
    }

    [Fact]
    public async Task AFavoriteSavedBeforeTheRunWasCancelledIsNotPutBackByTheNextRun()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var b = Movie(_serverB, primary: a);
        Group(a, b);
        var row = Row(_mario, old);
        row.Played = true;
        row.LastPlayedDate = _newer;
        row.IsFavorite = true;
        row.Rating = 6;
        _cancellingSaves.Add(b.Id);
        var move = Move(old, oldStub, (a, _serverA), (b, _serverB));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CarryAsync(move));
        Assert.True(Row(_mario, a).IsFavorite);

        Row(_mario, a).IsFavorite = false;
        Row(_mario, a).Rating = null;
        _cancellingSaves.Clear();
        _log.Dispose();
        _log = new WatchStateCarryLog(Path.Combine(_data, "log"), new CapturingLogger<WatchStateCarryLog>());
        var report = await CarryAsync(move);

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.True(Row(_mario, b).Played);
        Assert.False(Row(_mario, a).IsFavorite);
        Assert.Null(Row(_mario, a).Rating);
    }

    [Fact]
    public async Task ARunCancelledAfterItsLastSaveStillRecordsItsCarries()
    {
        // Nothing in the run checks the token after the last row, so only the save of the record could notice it.
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        Row(_luigi, old).IsFavorite = true;
        _cancelOnSave = a.Id;

        var report = await CarryAsync(Move(old, oldStub, (a, _serverA)));

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.True(Row(_luigi, a).IsFavorite);
        _log.Dispose();
        _log = new WatchStateCarryLog(Path.Combine(_data, "log"), new CapturingLogger<WatchStateCarryLog>());
        Assert.True(_log.TryFind(_luigi.Id, old.Id, out _));
    }

    [Fact]
    public async Task AMergeLeavesThePositionAndPutsTheRestOnThePrimary()
    {
        // Two copies of one library become one entity (decision 172): as Q47, but each version keeps its position.
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var b = Movie(_serverB, primary: a);
        Group(a, b);
        var row = Row(_mario, old);
        row.Played = true;
        row.PlayCount = 2;
        row.PlaybackPositionTicks = 500;
        row.LastPlayedDate = _newer;
        row.IsFavorite = true;
        row.Rating = 8;
        await Served(_mario, _serverB);

        var report = await CarryAsync(WatchStateMove.OfMerge(oldStub, old.Id, [new WatchStateTarget(a.Id, _serverA), new WatchStateTarget(b.Id, _serverB)]));

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.True(Row(_mario, a).IsFavorite);
        Assert.Equal(8, Row(_mario, a).Rating);
        Assert.Equal(2, Row(_mario, a).PlayCount);
        Assert.All([a, b], version => Assert.True(Row(_mario, version).Played));
        Assert.All([a, b], version => Assert.Equal(0, Row(_mario, version).PlaybackPositionTicks));
        Assert.False(Row(_mario, b).IsFavorite);
        Assert.Equal(500, Row(_mario, old).PlaybackPositionTicks);
    }

    [Fact]
    public async Task AMergedVersionIsNotATargetOfItsOwnMove()
    {
        var a = Movie(_serverA);
        var b = Movie(_serverB, primary: a);
        var bStub = _stubs.Last();
        Group(a, b);
        var row = Row(_mario, b);
        row.PlayCount = 3;
        row.PlaybackPositionTicks = 400;
        row.LastPlayedDate = _newer;
        row.IsFavorite = true;

        var report = await CarryAsync(WatchStateMove.OfMerge(bStub, b.Id, [new WatchStateTarget(a.Id, _serverA), new WatchStateTarget(b.Id, _serverB)]));

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.True(Row(_mario, a).IsFavorite);
        Assert.Equal(3, Row(_mario, a).PlayCount);
        Assert.Equal(0, Row(_mario, a).PlaybackPositionTicks);
        Assert.Equal(400, Row(_mario, b).PlaybackPositionTicks);
    }

    [Fact]
    public async Task AnOldItemRemovedAfterItsCarryForgetsItsRecordsSoThatThePathCanComeBack()
    {
        // A to B, then B to A, then A to B again with no run in between: the last move starts fresh (decisions 179, 187).
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;
        Row(_mario, old).Rating = 7;

        var report = await CarryAsync(RemovingMove(old, oldStub, (a, _serverA)));

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.True(Row(_mario, a).IsFavorite);
        Assert.Equal([old.Id], report.ForgettableOldItemIds);
        Assert.True(_log.TryFind(_mario.Id, old.Id, out _));

        Gone(old);
        Assert.Equal([old.Id], await ForgetRemovedAsync(report.ForgettableOldItemIds));

        Assert.False(_log.TryFind(_mario.Id, old.Id, out _));
        _items[old.Id] = old;
        var b = Movie(_serverA);
        report = await CarryAsync(RemovingMove(old, oldStub, (b, _serverA)));

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.True(Row(_mario, b).IsFavorite);
        Assert.Equal(7, Row(_mario, b).Rating);
    }

    [Fact]
    public async Task AnOldItemWhoseRemovalFailedKeepsItsRecordsAndIsNotCarriedAgain()
    {
        // The caller could not remove the old item, so it never forgets: a later run follows decision 155 (decision 187).
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var row = Row(_mario, old);
        row.Played = true;
        row.LastPlayedDate = _older;
        row.IsFavorite = true;
        row.Rating = 7;
        var move = RemovingMove(old, oldStub, (a, _serverA));

        var report = await CarryAsync(move);

        Assert.Equal([old.Id], report.ForgettableOldItemIds);
        _log.Dispose();
        _log = new WatchStateCarryLog(Path.Combine(_data, "log"), new CapturingLogger<WatchStateCarryLog>());
        Assert.True(_log.TryFind(_mario.Id, old.Id, out _));

        Row(_mario, a).IsFavorite = false;
        Row(_mario, a).Rating = null;
        _saves.Clear();
        report = await CarryAsync(move);

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.Empty(_saves);
        Assert.False(Row(_mario, a).IsFavorite);
        Assert.Null(Row(_mario, a).Rating);
    }

    [Fact]
    public async Task AnOldItemThatStaysKeepsItsRecordsAndIsNotCarriedAgain()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;
        var first = await CarryAsync(Move(old, oldStub, (a, _serverA)));

        var b = Movie(_serverA);
        var report = await CarryAsync(Move(old, oldStub, (b, _serverA)));

        Assert.Empty(first.ForgettableOldItemIds);
        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.Empty(report.ForgettableOldItemIds);
        Assert.True(_log.TryFind(_mario.Id, old.Id, out _));
        Assert.False(Row(_mario, b).IsFavorite);
    }

    [Fact]
    public async Task TheRecordsForgottenAfterARemovalStayForgottenOnDisk()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var (other, otherStub) = Old();
        var b = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;
        Row(_mario, other).IsFavorite = true;
        await CarryAsync(Move(old, oldStub, (a, _serverA)));

        var report = await CarryAsync(RemovingMove(old, oldStub, (a, _serverA)), Move(other, otherStub, (b, _serverA)));
        Gone(old);
        await ForgetRemovedAsync(report.ForgettableOldItemIds);
        _log.Dispose();
        _log = new WatchStateCarryLog(Path.Combine(_data, "log"), new CapturingLogger<WatchStateCarryLog>());

        Assert.Equal([old.Id], report.ForgettableOldItemIds);
        Assert.False(_log.TryFind(_mario.Id, old.Id, out _));
        Assert.True(_log.TryFind(_mario.Id, other.Id, out _));
    }

    [Fact]
    public async Task ARemovingCarryNotDoneForEveryUserIsNotForgettable()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;
        Row(_luigi, old).IsFavorite = true;
        _hidden.Add((_luigi.Id, a.Id));
        var move = RemovingMove(old, oldStub, (a, _serverA));

        var report = await CarryAsync(move);

        Assert.Equal(WatchStateMoveStatus.UsersLeft, report.Moves.Single().Status);
        Assert.Empty(report.ForgettableOldItemIds);

        _hidden.Clear();
        report = await CarryAsync(move);
        Gone(old);
        await ForgetRemovedAsync(report.ForgettableOldItemIds);

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.True(Row(_luigi, a).IsFavorite);
        Assert.False(_log.TryFind(_mario.Id, old.Id, out _));
        Assert.False(_log.TryFind(_luigi.Id, old.Id, out _));
    }

    [Fact]
    public async Task ARemovingCarryToVersionsNotLinkedIsNotForgettable()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;
        await CarryAsync(Move(old, oldStub, (a, _serverA)));
        var b = Movie(_serverA);
        var c = Movie(_serverB);
        Group(b, c);

        var report = await CarryAsync(RemovingMove(old, oldStub, (b, _serverA), (c, _serverB)));

        Assert.Equal(WatchStateMoveStatus.NotLinked, report.Moves.Single().Status);
        Assert.Empty(report.ForgettableOldItemIds);
        Assert.True(_log.TryFind(_mario.Id, old.Id, out _));
    }

    [Fact]
    public async Task AnOldItemWithAMoveThatDoesNotRemoveItIsNotForgettable()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var b = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;

        var report = await CarryAsync(RemovingMove(old, oldStub, (a, _serverA)), Move(old, oldStub, (b, _serverA)));

        Assert.Equal((2, 0), (report.Carried, report.Skipped));
        Assert.Empty(report.ForgettableOldItemIds);
        Assert.True(_log.TryFind(_mario.Id, old.Id, out _));
    }

    [Fact]
    public async Task ARemovingCarryWhoseRecordCannotBeSavedIsNotForgettable()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;
        _log.Dispose();
        Directory.CreateDirectory(_data);
        await File.WriteAllTextAsync(Path.Combine(_data, "log"), "in the way");
        _log = new WatchStateCarryLog(Path.Combine(_data, "log"), new CapturingLogger<WatchStateCarryLog>());
        var move = RemovingMove(old, oldStub, (a, _serverA));

        var report = await CarryAsync(move);

        Assert.Equal(WatchStateMoveStatus.Failed, report.Moves.Single().Status);
        Assert.Empty(report.ForgettableOldItemIds);
        Assert.True(_log.TryFind(_mario.Id, old.Id, out _));

        // A run again does not put back the favorite the user took off meanwhile.
        Row(_mario, a).IsFavorite = false;
        _saves.Clear();
        File.Delete(Path.Combine(_data, "log"));
        report = await CarryAsync(move);

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.Equal([old.Id], report.ForgettableOldItemIds);
        Assert.Empty(_saves);
        Assert.False(Row(_mario, a).IsFavorite);
    }

    [Fact]
    public async Task ACancelledRemovingCarryKeepsItsRecordsAndALaterRunDoesNotPutBackTheFavorite()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var (other, otherStub) = Old();
        var b = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;
        Row(_mario, other).IsFavorite = true;
        _cancellingSaves.Add(b.Id);
        var move = RemovingMove(old, oldStub, (a, _serverA));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CarryAsync(move, RemovingMove(other, otherStub, (b, _serverA))));

        _log.Dispose();
        _log = new WatchStateCarryLog(Path.Combine(_data, "log"), new CapturingLogger<WatchStateCarryLog>());
        Assert.True(_log.TryFind(_mario.Id, old.Id, out _));
        Row(_mario, a).IsFavorite = false;
        _saves.Clear();
        _cancellingSaves.Clear();
        var report = await CarryAsync(move);

        Assert.Empty(_saves);
        Assert.False(Row(_mario, a).IsFavorite);
        Assert.Equal([old.Id], report.ForgettableOldItemIds);
    }

    [Fact]
    public async Task ForgettingWithACancelledTokenKeepsTheRecords()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;
        var report = await CarryAsync(RemovingMove(old, oldStub, (a, _serverA)));
        Gone(old);
        await _cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ForgetRemovedAsync(report.ForgettableOldItemIds));

        Assert.True(_log.TryFind(_mario.Id, old.Id, out _));
    }

    [Fact]
    public async Task ALogThatCannotBeReadForNowForgetsNothing()
    {
        var log = new Mock<IWatchStateCarryLog>();
        log.Setup(item => item.EnsureLoaded()).Returns(false);
        var carrier = new WatchStateCarrier(_registry, _store, _library.Object, _users.Object, _userData.Object, _lastServed, log.Object, _carrierLogger);

        Assert.Empty(await carrier.ForgetRemovedAsync([Guid.NewGuid()], CancellationToken.None));

        log.Verify(item => item.Forget(It.IsAny<Func<Guid, Guid, bool>>()), Times.Never);
        log.Verify(item => item.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NothingToForgetIsDoneEvenWithALogThatCannotBeRead()
    {
        var log = new Mock<IWatchStateCarryLog>();
        log.Setup(item => item.EnsureLoaded()).Returns(false);
        var carrier = new WatchStateCarrier(_registry, _store, _library.Object, _users.Object, _userData.Object, _lastServed, log.Object, _carrierLogger);

        Assert.Empty(await carrier.ForgetRemovedAsync([], CancellationToken.None));

        log.Verify(item => item.Forget(It.IsAny<Func<Guid, Guid, bool>>()), Times.Never);
        Assert.Empty(_carrierLogger.Lines);
    }

    [Fact]
    public async Task NoUsersForgetsNothing()
    {
        // An empty user list may be a moment the users cannot be read; the records wait for a list again.
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;
        var report = await CarryAsync(RemovingMove(old, oldStub, (a, _serverA)));
        Gone(old);
        _everyone.Clear();

        Assert.Empty(await ForgetRemovedAsync(report.ForgettableOldItemIds));

        Assert.True(_log.TryFind(_mario.Id, old.Id, out _));
    }

    [Fact]
    public async Task ARemovingCarryWithNoUsersIsNotForgettable()
    {
        // With no users in the run nothing is carried for anyone, so no old item may forget its records (decision 203).
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        _everyone.Clear();

        var report = await CarryAsync(RemovingMove(old, oldStub, (a, _serverA)));

        Assert.Empty(report.ForgettableOldItemIds);
    }

    [Fact]
    public async Task AnOldItemStillInTheLibraryKeepsItsRecordsUntilItIsGone()
    {
        // Only the admin removes the old item, so the carrier checks it is gone before forgetting (decision 204).
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var (other, otherStub) = Old();
        var b = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;
        Row(_mario, other).IsFavorite = true;
        var report = await CarryAsync(RemovingMove(old, oldStub, (a, _serverA)), RemovingMove(other, otherStub, (b, _serverA)));
        Gone(other);

        Assert.Equal([other.Id], await ForgetRemovedAsync(report.ForgettableOldItemIds));

        Assert.True(_log.TryFind(_mario.Id, old.Id, out _));
        Assert.False(_log.TryFind(_mario.Id, other.Id, out _));
        await RestartAsync();
        Assert.True(_log.TryFind(_mario.Id, old.Id, out _));

        Gone(old);
        Assert.Equal([old.Id], await ForgetRemovedAsync([old.Id]));

        Assert.False(_log.TryFind(_mario.Id, old.Id, out _));
    }

    [Fact]
    public async Task AnOldItemKeptByTheForgettingIsPrunedByALaterRunOnceGone()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        var (other, otherStub) = Old();
        var b = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;
        Row(_mario, other).IsFavorite = true;
        var report = await CarryAsync(RemovingMove(old, oldStub, (a, _serverA)));
        Assert.Empty(await ForgetRemovedAsync(report.ForgettableOldItemIds));

        Gone(old);
        await CarryAsync(Move(other, otherStub, (b, _serverA)));

        Assert.False(_log.TryFind(_mario.Id, old.Id, out _));
    }

    [Fact]
    public async Task AnOldItemThatCannotBeLookedUpKeepsItsRecords()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;
        var report = await CarryAsync(RemovingMove(old, oldStub, (a, _serverA)));
        _failingLookups.Add(old.Id);

        Assert.Empty(await ForgetRemovedAsync(report.ForgettableOldItemIds));

        Assert.True(_log.TryFind(_mario.Id, old.Id, out _));
        Assert.DoesNotContain(_carrierLogger.Lines, line => line.Contains("/secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForgettingWhoseSaveFailsKeepsTheRecordsOnDisk()
    {
        var (old, oldStub) = Old();
        var a = Movie(_serverA);
        Row(_mario, old).IsFavorite = true;
        var move = RemovingMove(old, oldStub, (a, _serverA));
        var report = await CarryAsync(move);
        var folder = Path.Combine(_data, "log");
        var blocker = Path.Combine(folder, WatchStateCarryLog.FileName + ".tmp");
        Directory.CreateDirectory(blocker);
        Gone(old);

        Assert.Empty(await ForgetRemovedAsync(report.ForgettableOldItemIds));

        Assert.Contains(_carrierLogger.Lines, line => line.Contains("forgetting", StringComparison.Ordinal));
        Assert.DoesNotContain(_carrierLogger.Lines, line => line.Contains(_data, StringComparison.Ordinal));
        Directory.Delete(blocker);
        _log.Dispose();
        _log = new WatchStateCarryLog(folder, new CapturingLogger<WatchStateCarryLog>());
        Assert.True(_log.TryFind(_mario.Id, old.Id, out _));
        _items[old.Id] = old;
        Row(_mario, a).IsFavorite = false;
        _saves.Clear();
        await CarryAsync(move);

        Assert.Empty(_saves);
        Assert.False(Row(_mario, a).IsFavorite);
    }

    [Fact]
    public async Task ARemovingSeriesCarryIsForgettable()
    {
        var (old, oldStub) = OldSeries();
        var a = Series(_serverA);
        Row(_mario, old).IsFavorite = true;

        var report = await CarryAsync(WatchStateMove.OfSeries(oldStub, old.Id, [new WatchStateTarget(a.Id, _serverA)], removesOldItem: true));

        Assert.Equal(WatchStateMoveStatus.Carried, report.Moves.Single().Status);
        Assert.True(Row(_mario, a).IsFavorite);
        Assert.Equal([old.Id], report.ForgettableOldItemIds);
        Assert.True(_log.TryFind(_mario.Id, old.Id, out _));

        Gone(old);
        await ForgetRemovedAsync(report.ForgettableOldItemIds);

        Assert.False(_log.TryFind(_mario.Id, old.Id, out _));
    }

    [Fact]
    public void OnlyAMoveMadeAsRemovingTheOldItemSaysSo()
    {
        var (old, oldStub) = Old();
        var (series, seriesStub) = OldSeries();
        List<WatchStateTarget> targets = [new WatchStateTarget(Guid.NewGuid(), _serverA)];

        Assert.False(WatchStateMove.Of(oldStub, old.Id, targets).RemovesOldItem);
        Assert.True(WatchStateMove.Of(oldStub, old.Id, targets, removesOldItem: true).RemovesOldItem);
        Assert.False(WatchStateMove.OfSeries(seriesStub, series.Id, targets).RemovesOldItem);
        Assert.True(WatchStateMove.OfSeries(seriesStub, series.Id, targets, removesOldItem: true).RemovesOldItem);
        Assert.False(WatchStateMove.OfMerge(oldStub, old.Id, targets).RemovesOldItem);
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

    // A move whose old item is removed right after, as a rename or the automatic migration (decision 179).
    private static WatchStateMove RemovingMove(BaseItem from, StubEntry stub, params (BaseItem Item, Guid? Server)[] versions) =>
        WatchStateMove.Of(stub, from.Id, versions.Select(version => new WatchStateTarget(version.Item.Id, version.Server)).ToList(), removesOldItem: true);

    private static void Link(Video primary, Video alternate)
    {
        alternate.SetPrimaryVersionId(primary.Id);
        primary.LinkedAlternateVersions = [.. primary.LinkedAlternateVersions, new LinkedChild { ItemId = alternate.Id, Type = LinkedChildType.LinkedAlternateVersion }];
    }

    private async Task<WatchStateCarryReport> CarryAsync(params WatchStateMove[] moves)
    {
        await _registry.SaveAsync(StubState.Empty with { Stubs = _stubs.ToList(), Series = _series.ToList(), Groups = _groups.ToList() }, CancellationToken.None);
        var carrier = new WatchStateCarrier(_registry, _store, _library.Object, _users.Object, _userData.Object, _lastServed, _log, _carrierLogger);
        return await carrier.CarryAsync(moves, _cancellation.Token);
    }

    // Asks to forget the records of old items, as the caller does once it has removed them (decisions 187, 204).
    private Task<IReadOnlyList<Guid>> ForgetRemovedAsync(IReadOnlyCollection<Guid> oldItemIds)
    {
        var carrier = new WatchStateCarrier(_registry, _store, _library.Object, _users.Object, _userData.Object, _lastServed, _log, _carrierLogger);
        return carrier.ForgetRemovedAsync(oldItemIds, _cancellation.Token);
    }

    // The old item is no longer in any library, as after the admin removed it and Jellyfin scanned (decision 204).
    private void Gone(BaseItem item) => _items.Remove(item.Id);

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
