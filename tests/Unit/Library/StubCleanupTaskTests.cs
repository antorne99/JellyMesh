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
    }

    public void Dispose() => _fixture.Dispose();

    private StubCleanupTask Task() => new(
        _fixture.Registry,
        _fixture.Resolver,
        _fixture.Sync.Store,
        _fixture.Library.Object,
        _users.Object,
        _userData.Object,
        _providers.Object,
        Mock.Of<IFileSystem>(),
        _fixture.Remover,
        new CapturingLogger<StubCleanupTask>());

    private Task RunAsync() => Task().Run(new Progress<double>(), CancellationToken.None);

    private async Task<(Movie Stub, Movie Local)> FilmWithLocalCopyAsync()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m", "Film", 2020, "1"));
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();

        var path = _fixture.Path_("Film", "Film (2020)", "Film (2020).strm");
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

    [Fact]
    public async Task TheStateIsCopiedPerUserBeforeTheStubGoes()
    {
        var (stub, local) = await FilmWithLocalCopyAsync();
        var when = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var annaLocal = new UserItemData { Key = "l" };
        GiveState(_anna, stub, Played(when));
        GiveState(_anna, local, annaLocal);
        GiveState(_bruno, stub, new UserItemData { Key = "k", PlaybackPositionTicks = 500, LastPlayedDate = when });
        var brunoLocal = new UserItemData { Key = "l" };
        GiveState(_bruno, local, brunoLocal);

        await RunAsync();

        Assert.True(annaLocal.Played);
        Assert.Equal(500, brunoLocal.PlaybackPositionTicks);
        Assert.Equal(
            ["save anna /media/film/Film.mkv", "save bruno /media/film/Film.mkv", "delete " + stub.Path],
            _calls);
        Assert.False(Directory.Exists(_fixture.Path_("Film", "Film (2020)")));
        Assert.Empty(_fixture.Registry.State.Stubs);
    }

    [Fact]
    public async Task ANewerLocalStateIsNotOverwritten()
    {
        var (stub, local) = await FilmWithLocalCopyAsync();
        GiveState(_anna, stub, Played(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var newer = new UserItemData { Key = "l", PlaybackPositionTicks = 7, LastPlayedDate = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) };
        GiveState(_anna, local, newer);

        await RunAsync();

        Assert.False(newer.Played);
        Assert.Equal(["delete " + stub.Path], _calls);
    }

    [Fact]
    public async Task IfTheStateCannotBeSavedTheStubStays()
    {
        var (stub, local) = await FilmWithLocalCopyAsync();
        GiveState(_anna, stub, Played(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        GiveState(_anna, local, new UserItemData { Key = "l" });
        _userData
            .Setup(data => data.SaveUserData(_anna, local, It.IsAny<UserItemData>(), UserDataSaveReason.Import, It.IsAny<CancellationToken>()))
            .Throws(new IOException("disk"));

        await RunAsync();

        Assert.True(File.Exists(stub.Path));
        Assert.Single(_fixture.Registry.State.Stubs);
    }

    [Fact]
    public async Task ALocalItemThatCannotBeFoundKeepsTheStub()
    {
        var (stub, local) = await FilmWithLocalCopyAsync();
        _fixture.Library.Setup(library => library.GetItemById(local.Id)).Returns((BaseItem)null!);

        await RunAsync();

        Assert.True(File.Exists(stub.Path));
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
    public async Task AStaleServerDoesNotStopALocalCopyFromReplacingTheStub()
    {
        var (stub, _) = await FilmWithLocalCopyAsync();
        _fixture.Sync.Client.OnPage = (_, _) => RemoteFailure.Unreachable;
        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);

        await RunAsync();

        Assert.False(File.Exists(stub.Path));
    }

    [Fact]
    public async Task ACorruptIndexRemovesNothing()
    {
        var (stub, _) = await FilmWithLocalCopyAsync();
        var resolver = new Mock<IFederationResolver>();
        var key = new ContentIdentity("movie|tmdb:1", "movie|tmdb:1", ContentKind.Movie, MatchKind.ProviderId);
        resolver.Setup(item => item.Resolve()).Returns(new FederationView(
            IndexHealth.Corrupt,
            1,
            [new ResolvedContent(key, true, null, []) { LocalItemIds = [Guid.NewGuid()] }],
            []));
        var task = new StubCleanupTask(
            _fixture.Registry,
            resolver.Object,
            _fixture.Sync.Store,
            _fixture.Library.Object,
            _users.Object,
            _userData.Object,
            _providers.Object,
            Mock.Of<IFileSystem>(),
            _fixture.Remover,
            new CapturingLogger<StubCleanupTask>());

        await task.Run(new Progress<double>(), CancellationToken.None);

        Assert.True(File.Exists(stub.Path));
    }

    [Fact]
    public async Task TheLastEpisodeTakesItsFoldersAndSeriesAway()
    {
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e1", "s", 1, 1), TestItems.Episode("e2", "s", 2, 1));
        await _fixture.Sync.AddServerAsync("A", "shows");
        await _fixture.SyncAndMaterializeAsync();
        var seasonRow = new Season { Id = Guid.NewGuid(), Path = _fixture.Path_("Serie", "Show (2020)", "Season 01") };
        _fixture.Library.Setup(library => library.FindByPath(seasonRow.Path, true)).Returns(seasonRow);
        var localSeries = new Series { Id = Guid.NewGuid(), Name = "Show" };
        var localEpisode = new Episode { Id = Guid.NewGuid(), SeriesId = localSeries.Id };
        _fixture.Library.Setup(library => library.GetItemById(localEpisode.Id)).Returns(localEpisode);
        _fixture.Library.Setup(library => library.GetItemById(localSeries.Id)).Returns(localSeries);
        var ids = new ContentIds("72", "tt71", "71");

        _fixture.LocalCatalog.Items.Add(new CatalogItem { ServerId = "local", ItemId = localEpisode.Id.ToString("N"), Kind = ContentKind.Episode, Ids = ids, SeasonNumber = 1, EpisodeNumber = 1 });
        await RunAsync();

        Assert.False(Directory.Exists(seasonRow.Path));
        Assert.True(Directory.Exists(_fixture.Path_("Serie", "Show (2020)", "Season 02")));
        Assert.Contains("delete " + seasonRow.Path, _calls);
        Assert.Single(_fixture.Registry.State.Series);

        var seriesRow = new Series { Id = Guid.NewGuid(), Path = _fixture.Path_("Serie", "Show (2020)") };
        _fixture.Library.Setup(library => library.FindByPath(seriesRow.Path, true)).Returns(seriesRow);
        var favorite = new UserItemData { Key = "k", IsFavorite = true };
        var localData = new UserItemData { Key = "l" };
        GiveState(_anna, seriesRow, favorite);
        GiveState(_anna, localSeries, localData);
        _fixture.LocalCatalog.Items.Add(new CatalogItem { ServerId = "local", ItemId = Guid.NewGuid().ToString("N"), Kind = ContentKind.Episode, Ids = ids, SeasonNumber = 2, EpisodeNumber = 1 });
        var localId2 = Guid.Parse(_fixture.LocalCatalog.Items[^1].ItemId);
        _fixture.Library.Setup(library => library.GetItemById(localId2)).Returns(new Episode { Id = localId2, SeriesId = localSeries.Id });

        await RunAsync();

        Assert.False(Directory.Exists(seriesRow.Path));
        Assert.Empty(_fixture.Registry.State.Series);
        Assert.Contains("delete " + seriesRow.Path, _calls);
        Assert.True(localData.IsFavorite);
    }

    [Fact]
    public async Task ANewStubSeriesGetsItsRefreshAfterTheScanOnce()
    {
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 1));
        await _fixture.Sync.AddServerAsync("A", "shows");
        await _fixture.SyncAndMaterializeAsync();

        await RunAsync();
        _providers.Verify(provider => provider.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()), Times.Never);
        Assert.True(Assert.Single(_fixture.Registry.State.Series).NeedsRefresh);

        var row = new Series { Id = Guid.NewGuid(), Path = _fixture.Path_("Serie", "Show (2020)") };
        _fixture.Library.Setup(library => library.FindByPath(row.Path, true)).Returns(row);
        await RunAsync();
        await RunAsync();

        _providers.Verify(
            provider => provider.QueueRefresh(row.Id, It.Is<MetadataRefreshOptions>(options => options.MetadataRefreshMode == MetadataRefreshMode.Default), It.IsAny<RefreshPriority>()),
            Times.Once);
        Assert.False(Assert.Single(_fixture.Registry.State.Series).NeedsRefresh);
    }
}
