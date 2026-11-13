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

public sealed class StubCleanupTaskTests : IDisposable
{
    private readonly StubFixture _fixture = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Mock<IProviderManager> _providers = new();
    private readonly Mock<IVersionLinker> _linker = new();
    private readonly Mock<ILayoutMigration> _migration = new();
    private readonly Mock<IServerStubRenamer> _renames = new();
    private readonly CapturingLogger<StubCleanupTask> _logger = new();
    private readonly List<string> _calls = [];
    private readonly User _anna = new("anna", "auth", "reset");
    private readonly User _bruno = new("bruno", "auth", "reset");

    public StubCleanupTaskTests()
    {
        _users.Setup(users => users.GetUsers()).Returns([_anna, _bruno]);
        _fixture.Library
            .Setup(library => library.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()))
            .Callback<BaseItem, DeleteOptions>((item, _) => _calls.Add("delete " + item.Path));
        _userData
            .Setup(data => data.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), UserDataSaveReason.Import, It.IsAny<CancellationToken>()))
            .Callback<User, BaseItem, UserItemData, UserDataSaveReason, CancellationToken>((user, item, _, _, _) => _calls.Add("save " + user.Username + " " + item.Path));
        _linker.Setup(linker => linker.DetachForRemovalAsync(It.IsAny<IReadOnlyCollection<StubEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    public void Dispose() => _fixture.Dispose();

    private StubCleanupTask Task() => new(
        _fixture.Registry,
        _fixture.Resolver,
        _fixture.Sync.Index,
        _fixture.Sync.Registry,
        _fixture.Sync.Store,
        _fixture.Library.Object,
        _users.Object,
        _userData.Object,
        _providers.Object,
        Mock.Of<IFileSystem>(),
        _fixture.Remover,
        _logger,
        _linker.Object,
        _migration.Object,
        _renames.Object);

    private Task RunAsync() => Task().Run(new Progress<double>(), CancellationToken.None);

    private async Task<(Movie Stub, Movie Local)> FilmWithLocalCopyAsync()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m", "Film", 2020, "1"));
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();

        var path = _fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm");
        var stub = new Movie { Id = Guid.NewGuid(), Path = path };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(stub);

        var local = new Movie { Id = Guid.NewGuid(), Path = "/media/film/Film.mkv" };
        _fixture.Library.Setup(library => library.GetItemById(local.Id)).Returns(local);
        _fixture.LocalCatalog.Items.Add(new CatalogItem { ServerId = "local", ItemId = local.Id.ToString("N"), Kind = ContentKind.Movie, Ids = new ContentIds("1") });
        return (stub, local);
    }

    private void GiveState(User user, BaseItem item, UserItemData data)
    {
        _userData.Setup(manager => manager.GetUserData(user, item)).Returns(data);
    }

    private static UserItemData Played(DateTime when) => new() { Key = "k", Played = true, PlayCount = 1, LastPlayedDate = when };

    // Every stub gets an item, as after a scan, so that a merge could go through.
    private void GiveEveryStubAnItem()
    {
        foreach (var stub in _fixture.Registry.State.Stubs)
        {
            var path = _fixture.Path_(stub.RelativePath);
            _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(new Movie { Id = Guid.NewGuid(), Path = path });
        }
    }

    [Fact]
    public async Task TheStubsOfTwoServersWithTheSameFilmBothStay()
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary("films-b", "Film B", "movies"));
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("a", "Film", 2020, "1"));
        _fixture.Sync.Client.Add("films-b", RemoteItemType.Movie, TestItems.Movie("b", "Film", 2020, "1"));
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.Sync.AddServerAsync("B", "films-b");
        await _fixture.SyncAndMaterializeAsync();
        GiveEveryStubAnItem();

        await RunAsync();

        Assert.Equal(2, _fixture.Registry.State.Stubs.Count);
        Assert.All(_fixture.Registry.State.Stubs, stub => Assert.True(File.Exists(_fixture.Path_(stub.RelativePath))));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task ALocalCopyLeavesTheStubAndItsStateWhereTheyAre()
    {
        var (stub, local) = await FilmWithLocalCopyAsync();
        GiveState(_anna, stub, Played(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var annaLocal = new UserItemData { Key = "l" };
        GiveState(_anna, local, annaLocal);

        await RunAsync();

        Assert.False(annaLocal.Played);
        Assert.Empty(_calls);
        Assert.True(File.Exists(stub.Path));
        Assert.Single(_fixture.Registry.State.Stubs);
    }

    [Fact]
    public async Task AStubOfTheFlatLayoutIsLeftAsItIs()
    {
        var (_, local) = await FilmWithLocalCopyAsync();
        var own = _fixture.Registry.State.Stubs[0];
        var flat = new StubEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = StubKind.Movie,
            RelativePath = Path.Combine("Film", "Film (2020)", "Film (2020).strm"),
            Sources = own.Sources,
            IdentityKey = own.IdentityKey
        };
        var path = _fixture.Path_(flat.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "url");
        var item = new Movie { Id = Guid.NewGuid(), Path = path };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        GiveState(_anna, item, Played(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        GiveState(_anna, local, new UserItemData { Key = "l" });
        var state = _fixture.Registry.State;
        await _fixture.Registry.SaveAsync(state with { Stubs = [.. state.Stubs, flat] }, CancellationToken.None);

        await RunAsync();

        Assert.True(File.Exists(path));
        Assert.Contains(_fixture.Registry.State.Stubs, stub => stub.Id == flat.Id);
        Assert.Empty(_calls);
    }

    // Two copies on one server, then a third that holds the IDs of both: one film from now on.
    private async Task<(Movie Survivor, Movie Merged)> TwoFilmsThatBecomeOneAsync()
    {
        var byTmdb = TestItems.Movie("a", "Film", 2020, "1");
        var byImdb = TestItems.Movie("b", "Film", 2020, "2") with { ProviderIds = new Dictionary<string, string?> { ["Imdb"] = "tt1" } };
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary("films-b", "Film B", "movies"));
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, byTmdb);
        _fixture.Sync.Client.Add("films-b", RemoteItemType.Movie, byImdb);
        await _fixture.Sync.AddServerAsync("A", "films", "films-b");
        await _fixture.SyncAndMaterializeAsync();
        Assert.Equal(2, _fixture.Registry.State.Stubs.Count(stub => stub.Sources.Any(source => source.ItemId is "a" or "b")));

        var both = TestItems.Movie("c", "Film", 2020, "1") with { ProviderIds = new Dictionary<string, string?> { ["Tmdb"] = "1", ["Imdb"] = "tt1" } };
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, both);
        await _fixture.SyncAndMaterializeAsync();
        var own = _fixture.Registry.State.Stubs.Where(stub => stub.Sources.Any(source => source.ItemId is "a" or "b" or "c")).ToList();
        Assert.Equal(2, own.Count);

        var claim = new StubMatcher(own).ClaimAll(_fixture.Resolver.Resolve().Contents).Single(claim => claim.Stub is not null);
        var survivor = new Movie { Id = Guid.NewGuid(), Path = _fixture.Path_(claim.Stub!.RelativePath) };
        var merged = new Movie { Id = Guid.NewGuid(), Path = _fixture.Path_(Assert.Single(claim.Merged).RelativePath) };
        _fixture.Library.Setup(library => library.FindByPath(survivor.Path, false)).Returns(survivor);
        _fixture.Library.Setup(library => library.FindByPath(merged.Path, false)).Returns(merged);
        return (survivor, merged);
    }

    [Fact]
    public async Task AStubMergedIntoAnother_HandsItsStateOverBeforeItGoes()
    {
        var (survivor, merged) = await TwoFilmsThatBecomeOneAsync();
        GiveState(_anna, merged, Played(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var kept = new UserItemData { Key = "s" };
        GiveState(_anna, survivor, kept);

        await RunAsync();

        Assert.True(kept.Played);
        Assert.Equal(["save anna " + survivor.Path, "delete " + merged.Path], _calls);
        Assert.Equal(survivor.Path, _fixture.Path_(Assert.Single(_fixture.Registry.State.Stubs).RelativePath));
        Assert.False(File.Exists(merged.Path));
    }

    // Q327: the merged stub leaves its version group, with the registry lock held, before it is deleted.
    [Fact]
    public async Task AMergedStubLeavesItsVersionGroupBeforeItGoes()
    {
        var (_, merged) = await TwoFilmsThatBecomeOneAsync();
        _linker.Setup(linker => linker.DetachForRemovalAsync(It.IsAny<IReadOnlyCollection<StubEntry>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyCollection<StubEntry> stubs, CancellationToken _) =>
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fixture.Registry.EnterAsync(wait.Token));
                _calls.Add("detach " + string.Join(",", stubs.Select(stub => _fixture.Path_(stub.RelativePath))));
                return (IReadOnlyList<StubEntry>)[];
            });

        await RunAsync();

        Assert.Equal(["detach " + merged.Path, "delete " + merged.Path], _calls);
        Assert.False(File.Exists(merged.Path));
    }

    // Q326 and Q327: a merged stub that cannot leave its group stays, with its state, for the next scan.
    [Fact]
    public async Task AMergedStubThatCannotLeaveItsVersionGroupStaysForTheNextScan()
    {
        var (survivor, merged) = await TwoFilmsThatBecomeOneAsync();
        GiveState(_anna, merged, Played(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var kept = new UserItemData { Key = "s" };
        GiveState(_anna, survivor, kept);
        _linker.Setup(linker => linker.DetachForRemovalAsync(It.IsAny<IReadOnlyCollection<StubEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<StubEntry> stubs, CancellationToken _) => stubs.ToList());

        await RunAsync();

        Assert.Empty(_calls);
        Assert.False(kept.Played);
        Assert.True(File.Exists(merged.Path));
        Assert.Equal(2, _fixture.Registry.State.Stubs.Count);

        _linker.Setup(linker => linker.DetachForRemovalAsync(It.IsAny<IReadOnlyCollection<StubEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        await RunAsync();

        Assert.False(File.Exists(merged.Path));
        Assert.Single(_fixture.Registry.State.Stubs);
    }

    [Fact]
    public async Task AMergeOnOneServerLeavesTheStubOfAnotherServer()
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary("films-c", "Film C", "movies"));
        _fixture.Sync.Client.Add("films-c", RemoteItemType.Movie, TestItems.Movie("d", "Film", 2020, "1"));
        var other = await _fixture.Sync.AddServerAsync("B", "films-c");
        var (survivor, merged) = await TwoFilmsThatBecomeOneAsync();
        var otherPath = _fixture.Path_(other, "Film", "Film (2020)", "Film (2020) - B.strm");
        _fixture.Library.Setup(library => library.FindByPath(otherPath, false)).Returns(new Movie { Id = Guid.NewGuid(), Path = otherPath });

        await RunAsync();

        Assert.Equal(["delete " + merged.Path], _calls);
        Assert.True(File.Exists(survivor.Path));
        Assert.True(File.Exists(otherPath));
        var stubs = _fixture.Registry.State.Stubs;
        Assert.Equal(2, stubs.Count);
        Assert.Equal(stubs.Select(stub => stub.Id).Order(StringComparer.Ordinal), Assert.Single(_fixture.Registry.State.Groups).StubIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AMergedStubWaitsWhileTheSurvivorHasNoItem()
    {
        var (survivor, merged) = await TwoFilmsThatBecomeOneAsync();
        _fixture.Library.Setup(library => library.FindByPath(survivor.Path, false)).Returns((BaseItem?)null);

        await RunAsync();

        Assert.Equal(2, _fixture.Registry.State.Stubs.Count);
        Assert.True(File.Exists(merged.Path));
    }

    [Fact]
    public async Task AfterTheScan_TheVersionsAreLinked_OnceTheMergedStubIsGone_OutsideTheRegistryLock()
    {
        await TwoFilmsThatBecomeOneAsync();
        var seen = new List<string>();
        _linker
            .Setup(linker => linker.LinkAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (bool thorough, CancellationToken token) =>
            {
                // The linker takes the same lock, so it must be free by now.
                await _fixture.Registry.EnterAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
                _fixture.Registry.Exit();
                seen.Add(thorough + " " + _fixture.Registry.State.Stubs.Count);
            });

        await RunAsync();

        Assert.Equal(["False 1"], seen);
    }

    [Fact]
    public async Task AfterTheLink_TheMigrationRuns_OutsideTheRegistryLock()
    {
        await TwoFilmsThatBecomeOneAsync();
        var seen = new List<string>();
        _linker
            .Setup(linker => linker.LinkAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => seen.Add("link"))
            .Returns(System.Threading.Tasks.Task.CompletedTask);
        _migration
            .Setup(migration => migration.RunAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken token) =>
            {
                // The migration takes the same lock, so it must be free by now.
                await _fixture.Registry.EnterAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
                _fixture.Registry.Exit();
                seen.Add("migration");
            });

        await RunAsync();

        Assert.Equal(["link", "migration"], seen);
    }

    [Fact]
    public async Task BeforeTheLink_TheRenamedStubsFinish_OutsideTheRegistryLock()
    {
        await TwoFilmsThatBecomeOneAsync();
        var seen = new List<string>();
        _linker
            .Setup(linker => linker.LinkAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => seen.Add("link"))
            .Returns(System.Threading.Tasks.Task.CompletedTask);
        _renames
            .Setup(renames => renames.FinishAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken token) =>
            {
                await _fixture.Registry.EnterAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
                _fixture.Registry.Exit();
                seen.Add("renames");
            });

        await RunAsync();

        Assert.Equal(["renames", "link"], seen);
    }

    [Fact]
    public async Task AFailedRenameFinish_IsLoggedByItsTypeOnly_AndTheLinkRuns()
    {
        await TwoFilmsThatBecomeOneAsync();
        _renames
            .Setup(renames => renames.FinishAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("/federata/secret"));

        await RunAsync();

        Assert.Contains(_logger.Lines, line => line.Contains(nameof(IOException), StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Lines, line => line.Contains("secret", StringComparison.Ordinal));
        _linker.Verify(linker => linker.LinkAsync(false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AFailedMigration_IsLoggedByItsTypeOnly()
    {
        await TwoFilmsThatBecomeOneAsync();
        _migration
            .Setup(migration => migration.RunAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("/federata/secret"));

        await RunAsync();

        Assert.Contains(_logger.Lines, line => line.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Lines, line => line.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedLink_IsLoggedByItsTypeOnly_AndTheScanGoesOn()
    {
        await TwoFilmsThatBecomeOneAsync();
        _linker
            .Setup(linker => linker.LinkAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("http://secret"));

        await RunAsync();

        Assert.Contains(_logger.Lines, line => line.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Lines, line => line.Contains("secret", StringComparison.Ordinal));
        Assert.Single(_fixture.Registry.State.Stubs);
    }

    [Fact]
    public async Task TheMaterializerAsksForTheScanThatHandlesAMerge()
    {
        await TwoFilmsThatBecomeOneAsync();
        _fixture.Library.Invocations.Clear();

        await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        _fixture.Library.Verify(library => library.QueueLibraryScan(), Times.Once);
    }

    [Fact]
    public async Task IfTheStateCannotBeSavedTheMergedStubStays()
    {
        var (survivor, merged) = await TwoFilmsThatBecomeOneAsync();
        GiveState(_anna, merged, Played(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        GiveState(_anna, survivor, new UserItemData { Key = "s" });
        _userData
            .Setup(data => data.SaveUserData(_anna, survivor, It.IsAny<UserItemData>(), UserDataSaveReason.Import, It.IsAny<CancellationToken>()))
            .Throws(new IOException("disk"));

        await RunAsync();

        Assert.True(File.Exists(merged.Path));
        Assert.Equal(2, _fixture.Registry.State.Stubs.Count);
    }

    [Fact]
    public async Task ACorruptIndexRemovesNothing()
    {
        var (_, merged) = await TwoFilmsThatBecomeOneAsync();
        var resolver = new Mock<IFederationResolver>();
        var key = new ContentIdentity("movie|tmdb:1", "movie|tmdb:1", ContentKind.Movie, MatchKind.ProviderId);
        resolver.Setup(item => item.Resolve(It.IsAny<IndexSnapshot>(), It.IsAny<IReadOnlyList<RemoteServerInfo>>())).Returns(new FederationView(
            IndexHealth.Corrupt,
            1,
            [new ResolvedContent(key, false, null, [])],
            []));
        var task = new StubCleanupTask(
            _fixture.Registry,
            resolver.Object,
            _fixture.Sync.Index,
            _fixture.Sync.Registry,
            _fixture.Sync.Store,
            _fixture.Library.Object,
            _users.Object,
            _userData.Object,
            _providers.Object,
            Mock.Of<IFileSystem>(),
            _fixture.Remover,
            new CapturingLogger<StubCleanupTask>());

        await task.Run(new Progress<double>(), CancellationToken.None);

        Assert.True(File.Exists(merged.Path));
        Assert.Equal(2, _fixture.Registry.State.Stubs.Count);
    }

    [Fact]
    public async Task WithoutALocalCopyNothingHappens()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();

        await RunAsync();

        Assert.Single(_fixture.Registry.State.Stubs);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task LocalEpisodesLeaveTheStubsTheirFoldersAndTheSeries()
    {
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e1", "s", 1, 1), TestItems.Episode("e2", "s", 1, 2) with { IndexNumberEnd = 3 });
        var a = await _fixture.Sync.AddServerAsync("A", "shows");
        await _fixture.SyncAndMaterializeAsync();
        var seriesRow = new Series { Id = Guid.NewGuid(), Path = _fixture.Path_(a, "Serie", "Show (2020)") };
        _fixture.Library.Setup(library => library.FindByPath(seriesRow.Path, true)).Returns(seriesRow);
        GiveState(_anna, seriesRow, new UserItemData { Key = "k", IsFavorite = true });
        GiveEveryStubAnItem();
        var localSeries = new Series { Id = Guid.NewGuid(), Name = "Show" };
        var localData = new UserItemData { Key = "l" };
        GiveState(_anna, localSeries, localData);
        _fixture.Library.Setup(library => library.GetItemById(localSeries.Id)).Returns(localSeries);
        foreach (var number in new[] { 1, 2, 3 })
        {
            var local = new Episode { Id = Guid.NewGuid(), SeriesId = localSeries.Id, Path = "/media/serie/e" + number + ".mkv" };
            _fixture.Library.Setup(library => library.GetItemById(local.Id)).Returns(local);
            _fixture.LocalCatalog.Items.Add(new CatalogItem
            {
                ServerId = "local",
                ItemId = local.Id.ToString("N"),
                Kind = ContentKind.Episode,
                Ids = new ContentIds("72", "tt71", "71"),
                SeasonNumber = 1,
                EpisodeNumber = number
            });
        }

        await RunAsync();

        Assert.Empty(_calls);
        Assert.False(localData.IsFavorite);
        Assert.Equal(2, _fixture.Registry.State.Stubs.Count);
        Assert.Single(_fixture.Registry.State.Series);
        Assert.True(File.Exists(_fixture.Path_(a, "Serie", "Show (2020)", "Season 01", "Show S01E01 - A.strm")));
        Assert.True(File.Exists(_fixture.Path_(a, "Serie", "Show (2020)", "Season 01", "Show S01E02-E03 - A.strm")));
    }

    [Fact]
    public async Task ANewStubSeriesGetsItsRefreshAfterTheScanOnce()
    {
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 1));
        var a = await _fixture.Sync.AddServerAsync("A", "shows");
        await _fixture.SyncAndMaterializeAsync();

        await RunAsync();
        _providers.Verify(provider => provider.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()), Times.Never);
        Assert.True(Assert.Single(_fixture.Registry.State.Series).NeedsRefresh);

        var row = new Series { Id = Guid.NewGuid(), Path = _fixture.Path_(a, "Serie", "Show (2020)") };
        _fixture.Library.Setup(library => library.FindByPath(row.Path, true)).Returns(row);
        await RunAsync();
        await RunAsync();

        _providers.Verify(
            provider => provider.QueueRefresh(row.Id, It.Is<MetadataRefreshOptions>(options => options.MetadataRefreshMode == MetadataRefreshMode.Default), It.IsAny<RefreshPriority>()),
            Times.Once);
        Assert.False(Assert.Single(_fixture.Registry.State.Series).NeedsRefresh);
    }
}
