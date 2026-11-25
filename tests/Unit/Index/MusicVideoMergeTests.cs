using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Index;

/// <summary>
/// Manual merges of music videos (row 454, D64), on a real resolver: a list of their own, merged and undone like films.
/// </summary>
public sealed class MusicVideoMergeTests : IDisposable
{
    private const string Library = TestLibraries.MusicVideos;
    private const long FourMinutes = 2_400_000_000;

    private readonly SyncFixture _fixture = new();
    private readonly List<CatalogItem> _local = [];
    private readonly InMemoryMergeStore _store = new();

    public MusicVideoMergeTests() => _fixture.Client.Libraries.Add(new(Library, "Clip", "musicvideos"));

    public void Dispose() => _fixture.Dispose();

    private FederationResolver Resolver() => new(_fixture.Index, new LocalCatalog(_local), _fixture.Registry, _store);

    private ManualMergeService Service() => new(Resolver(), _store, Mock.Of<ILibraryMaterializer>(), TimeProvider.System, new CapturingLogger<ManualMergeService>());

    private static RemoteItem Clip(string id, string? imdb, int height = 1080) => new()
    {
        Id = id,
        Name = "Song",
        ProductionYear = 2019,
        Artists = ["Singer"],
        RunTimeTicks = FourMinutes,
        ProviderIds = imdb is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["Imdb"] = imdb, ["Tvdb"] = "99" },
        MediaSources = [TestItems.Source(id, height, 5_000_000)]
    };

    private static CatalogItem LocalClip(Guid id, string imdb) => new()
    {
        ServerId = "local",
        ItemId = id.ToString("N"),
        Kind = ContentKind.MusicVideo,
        Title = "Song",
        Year = 2019,
        Artists = ["Singer"],
        Ids = new ContentIds(null, imdb)
    };

    // Two id sets and a copy without IDs between them: three titles only the administrator can join (D64).
    private async Task SyncCaseAsync(params RemoteItem[] clips)
    {
        _fixture.Client.Add(Library, RemoteItemType.MusicVideo, clips.Length > 0 ? clips : [Clip("a", "tt1", 2160), Clip("b", "tt2"), Clip("c", null, 720)]);
        await _fixture.AddServerAsync("Friend", Library);
        await _fixture.Sync.RunAsync(CancellationToken.None);
    }

    private string KeyOf(MergeCaseView found, string itemId) => found.Titles.Single(title => title.Copies.Any(copy => copy.Copy.ItemId == itemId)).WorkKey;

    [Fact]
    public async Task AMusicVideoCaseIsListedApartFromFilmsAndSeries()
    {
        await SyncCaseAsync();

        var list = Service().GetList();

        Assert.Empty(list.Cases);
        Assert.Empty(list.SeriesCases);
        var found = Assert.Single(list.MusicVideoCases);
        Assert.Equal(ContentKind.MusicVideo, found.ContentKind);
        Assert.Equal(3, found.Titles.Count);
    }

    [Fact]
    public async Task EachMusicVideoCopyShowsItsDurationAndVersions_AndAPreviewNamesWhatWouldPlay()
    {
        await SyncCaseAsync();
        var found = Assert.Single(Service().GetList().MusicVideoCases);
        var copy = found.Titles.SelectMany(title => title.Copies).Single(view => view.Copy.ItemId == "a");

        Assert.Equal(FourMinutes, copy.RunTimeTicks);
        Assert.Equal(2160, Assert.Single(copy.Versions).Quality.Height);
        Assert.Null(copy.Copy.Tvdb);

        var forecast = Service().Preview([KeyOf(found, "a"), KeyOf(found, "b")]).Forecast!;
        Assert.Equal("a", forecast.Copy!.Copy.ItemId);
        Assert.Equal("a", forecast.Version!.MediaSourceId);
    }

    [Fact]
    public async Task MusicVideosMergeAsOneTitleWithTheBestCopyPlaying_AndTheUndoSeparatesThem()
    {
        await SyncCaseAsync();
        var service = Service();
        var found = Assert.Single(service.GetList().MusicVideoCases);

        Assert.Equal(ManualMergeOutcome.Merged, await service.MergeAsync([KeyOf(found, "a"), KeyOf(found, "c")], CancellationToken.None));

        var merge = Assert.Single(_store.Merges);
        Assert.All(merge.Copies, copy => Assert.Equal(ContentKind.MusicVideo, copy.Kind));
        Assert.Equal(["a", "c"], merge.Copies.Select(copy => copy.ItemId).Order(StringComparer.Ordinal));
        var merged = Assert.Single(Resolver().Resolve().Contents, content => content.Identity.Match == MatchKind.Manual);
        Assert.Equal(ContentKind.MusicVideo, merged.Identity.Kind);
        Assert.Equal("a", merged.Winner!.ItemId);
        Assert.Equal("c", Assert.Single(merged.Backups).ItemId);

        Assert.True(await service.UndoAsync(merge.Id, CancellationToken.None));
        Assert.Equal(3, Resolver().Resolve().Contents.Count);
        Assert.Single(service.GetList().MusicVideoCases);
    }

    [Fact]
    public async Task TwoLocalMusicVideosCannotBeMerged_AsTwoLocalTitles()
    {
        _local.Add(LocalClip(Guid.NewGuid(), "tt1"));
        _local.Add(LocalClip(Guid.NewGuid(), "tt2"));
        await SyncCaseAsync(Clip("a", "tt1"), Clip("b", "tt2"), Clip("c", null));
        var service = Service();
        var found = Assert.Single(service.GetList().MusicVideoCases);

        var outcome = await service.MergeAsync([KeyOf(found, "a"), KeyOf(found, "b")], CancellationToken.None);

        Assert.Equal(ManualMergeOutcome.TwoLocalTitles, outcome);
        Assert.Empty(_store.Merges);
    }

    [Fact]
    public async Task AFilmAndAMusicVideoAreNeverOneCase_SoTheirMergeIsRefused()
    {
        RemoteItem Film(string id, string imdb) => TestItems.Movie(id, "Song", 2019) with { ProviderIds = new Dictionary<string, string?> { ["Imdb"] = imdb } };
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, Film("f1", "tt1"), Film("f2", "tt2"), TestItems.Movie("f3", "Song", 2019) with { ProviderIds = new Dictionary<string, string?>() });
        _fixture.Client.Add(Library, RemoteItemType.MusicVideo, Clip("a", "tt1"), Clip("b", "tt2"), Clip("c", null));
        await _fixture.AddServerAsync("Friend", TestLibraries.Films, Library);
        await _fixture.Sync.RunAsync(CancellationToken.None);
        var service = Service();
        var list = service.GetList();
        var films = Assert.Single(list.Cases);
        var clips = Assert.Single(list.MusicVideoCases);

        var outcome = await service.MergeAsync([KeyOf(films, "f1"), KeyOf(clips, "a")], CancellationToken.None);

        Assert.Equal(ManualMergeOutcome.ListChanged, outcome);
        Assert.Empty(_store.Merges);
        Assert.All(films.Titles.SelectMany(title => title.Copies), copy => Assert.Equal(ContentKind.Movie, copy.Copy.Kind));
    }

    [Fact]
    public async Task AMusicVideoMergeInEffectLeavesAFilmWithTheSameIdAlone()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("f1", "Song", 2019) with { ProviderIds = new Dictionary<string, string?> { ["Imdb"] = "tt1" } });
        _fixture.Client.Add(Library, RemoteItemType.MusicVideo, Clip("a", "tt1"), Clip("b", "tt2"), Clip("c", null));
        await _fixture.AddServerAsync("Friend", TestLibraries.Films, Library);
        await _fixture.Sync.RunAsync(CancellationToken.None);
        var service = Service();
        var found = Assert.Single(service.GetList().MusicVideoCases);

        Assert.Equal(ManualMergeOutcome.Merged, await service.MergeAsync([KeyOf(found, "a"), KeyOf(found, "c")], CancellationToken.None));

        var film = Assert.Single(Resolver().Resolve().Contents, content => content.Identity.Kind == ContentKind.Movie);
        Assert.NotEqual(MatchKind.Manual, film.Identity.Match);
        Assert.Equal(["f1"], film.RemoteItems.Select(item => item.ItemId));
    }

    [Fact]
    public void ALocalMusicVideoAwaitingItsRefreshIsListedByItsOwnId()
    {
        var id = Guid.NewGuid();
        _local.Add(LocalClip(id, "tt1") with { AwaitsRefresh = true });

        var view = Resolver().Resolve();

        Assert.Equal([id], view.LocalItemsAwaitingRefresh);
    }

    private sealed class LocalCatalog(IReadOnlyList<CatalogItem> items) : ILocalCatalogReader
    {
        public IReadOnlyList<CatalogItem> Read() => items;
    }
}
