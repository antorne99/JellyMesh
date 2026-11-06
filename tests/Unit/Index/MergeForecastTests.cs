using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
/// What the merge list says about quality and what would play, on a real resolver and a real comparison. The chain has
/// three titles, and any two of them can be merged.
/// </summary>
public sealed class MergeForecastTests : IDisposable
{
    private const string Library = "films";

    private readonly SyncFixture _fixture = new();
    private readonly List<CatalogItem> _local = [];
    private readonly InMemoryMergeStore _store = new();

    public void Dispose() => _fixture.Dispose();

    private ManualMergeService Service()
    {
        var resolver = new FederationResolver(_fixture.Index, new LocalCatalog(_local), _fixture.Registry, _store);
        return new ManualMergeService(resolver, _store, Mock.Of<ILibraryMaterializer>(), TimeProvider.System, new CapturingLogger<ManualMergeService>());
    }

    private static RemoteItem Film(string id, string tmdb, string imdb, params RemoteMediaSource[] sources) => TestItems.Movie(id, "Film " + id) with
    {
        ProviderIds = new Dictionary<string, string?> { ["Tmdb"] = tmdb, ["Imdb"] = imdb },
        MediaSources = sources
    };

    // The three films are linked by IDs that contradict each other, so they stay apart as one case.
    private async Task SyncChainAsync(RemoteMediaSource first, RemoteMediaSource second, RemoteMediaSource third)
    {
        _fixture.Client.Add(Library, RemoteItemType.Movie, Film("a", "1", "tt1", first), Film("b", "1", "tt2", second), Film("c", "2", "tt2", third));
        await _fixture.AddServerAsync("Friend", Library);
        await _fixture.Sync.RunAsync(CancellationToken.None);
    }

    private static RemoteMediaSource NoVideo(string id) => new() { Id = id, Name = "Version", MediaStreams = [] };

    private MergeCaseView Case() => Assert.Single(Service().GetList().Cases);

    private string KeyOf(string itemId) => Case().Titles.Single(title => title.Copies.Any(copy => copy.Copy.ItemId == itemId)).WorkKey;

    private MergeForecast Forecast(params string[] itemIds) =>
        Service().Preview(itemIds.Select(KeyOf).ToList()).Forecast ?? throw new InvalidOperationException("The merge was refused.");

    [Fact]
    public async Task EachCopyHasItsVersionsWithQualityBestFirst()
    {
        await SyncChainAsync(
            TestItems.Source("a", 2160, 20_000_000, "hevc", "HDR"),
            TestItems.Source("b", 1080, 5_000_000),
            TestItems.Source("c", 720, 2_000_000));

        var copies = Case().Titles.SelectMany(title => title.Copies).ToList();

        var quality = Assert.Single(copies.Single(copy => copy.Copy.ItemId == "a").Versions).Quality;
        Assert.Equal(new SourceQuality(2160, true, 20_000_000, "hevc"), quality);
    }

    [Fact]
    public async Task ACopyWithSeveralVersionsListsThemBestFirst()
    {
        var film = Film("a", "1", "tt1", TestItems.Source("low", 720, 2_000_000), TestItems.Source("high", 1080, 5_000_000));
        _fixture.Client.Add(Library, RemoteItemType.Movie, film, Film("b", "1", "tt2", TestItems.Source("b", 720, 1)), Film("c", "2", "tt2", TestItems.Source("c", 720, 1)));
        await _fixture.AddServerAsync("Friend", Library);
        await _fixture.Sync.RunAsync(CancellationToken.None);

        var copy = Case().Titles.SelectMany(title => title.Copies).Single(found => found.Copy.ItemId == "a");

        Assert.Equal(["high", "low"], copy.Versions.Select(version => version.MediaSourceId));
    }

    [Fact]
    public async Task AVersionWithoutVideoShowsEveryValueAsMissing()
    {
        await SyncChainAsync(NoVideo("a"), TestItems.Source("b", 1080, 5_000_000), TestItems.Source("c", 720, 2_000_000));

        var quality = Assert.Single(Case().Titles.SelectMany(title => title.Copies).Single(copy => copy.Copy.ItemId == "a").Versions).Quality;

        Assert.Null(quality.Height);
        Assert.Null(quality.BitRate);
        Assert.Null(quality.Codec);
        Assert.False(quality.IsRangeKnown);
    }

    [Fact]
    public async Task TheForecastNamesTheBestQuality()
    {
        await SyncChainAsync(TestItems.Source("a", 720, 9_000_000), TestItems.Source("b", 1080, 2_000_000), TestItems.Source("c", 480, 9_000_000));

        var forecast = Forecast("a", "b");

        Assert.Equal("b", forecast.Copy!.Copy.ItemId);
        Assert.Equal("b", forecast.Version!.MediaSourceId);
        Assert.Equal("Friend", forecast.Copy.ServerName);
        Assert.True(forecast.IsCertain);
    }

    [Fact]
    public async Task TheForecastFollowsTheSelectionBecauseTheBitRateWindowDoes()
    {
        await SyncChainAsync(
            TestItems.Source("a", 1080, 10_000_000, "h264"),
            TestItems.Source("b", 1080, 9_600_000, "hevc"),
            TestItems.Source("c", 1080, 9_400_000, "av1"));

        Assert.Equal("b", Forecast("a", "b", "c").Copy!.Copy.ItemId);
        Assert.Equal("c", Forecast("b", "c").Copy!.Copy.ItemId);
    }

    [Theory]
    [InlineData("a", "b")]
    [InlineData("b", "c")]
    [InlineData("a", "b", "c")]
    public async Task TheForecastIsWhatTheMergeThenPlays(params string[] itemIds)
    {
        await SyncChainAsync(
            TestItems.Source("a", 1080, 10_000_000, "h264"),
            TestItems.Source("b", 1080, 9_600_000, "hevc"),
            TestItems.Source("c", 1080, 9_400_000, "av1"));
        var keys = itemIds.Select(KeyOf).ToList();
        var service = Service();
        var forecast = service.Preview(keys).Forecast!;

        Assert.Equal(ManualMergeOutcome.Merged, await service.MergeAsync(keys, CancellationToken.None));

        var resolver = new FederationResolver(_fixture.Index, new LocalCatalog(_local), _fixture.Registry, _store);
        var winners = resolver.Resolve().Contents.Where(content => content.Identity.Match == MatchKind.Manual).Select(content => content.Winner!);
        var winner = Assert.Single(winners);
        Assert.Equal(forecast.Copy!.Copy.ItemId, winner.ItemId);
        Assert.Equal(forecast.Version!.MediaSourceId, winner.MediaSourceId);
    }

    [Fact]
    public async Task ALocalCopyWinsWhateverTheQualityOfTheOthers()
    {
        await SyncChainAsync(TestItems.Source("a", 2160, 90_000_000), TestItems.Source("b", 720, 1_000_000), TestItems.Source("c", 720, 1_000_000));
        _local.Add(new CatalogItem { ServerId = "local", ItemId = "here", Kind = ContentKind.Movie, Title = "Film a", Ids = new ContentIds("1", "tt1") });

        var titles = Case().Titles;
        var withLocal = titles.Single(title => title.HasLocalCopy);
        var forecast = Service().Preview([withLocal.WorkKey, titles.First(title => !title.HasLocalCopy).WorkKey]).Forecast!;

        Assert.True(forecast.Copy!.Copy.IsLocal);
        Assert.Null(forecast.Version);
        Assert.True(forecast.IsCertain);
        Assert.Empty(forecast.Copy.Versions);
    }

    [Fact]
    public async Task AMissingValueThatCouldDecideMakesTheForecastUncertain()
    {
        await SyncChainAsync(TestItems.Source("a", 720, 9_000_000), NoVideo("b"), TestItems.Source("c", 720, 1_000_000));

        var forecast = Forecast("a", "b");

        Assert.Equal("a", forecast.Copy!.Copy.ItemId);
        Assert.False(forecast.IsCertain);
    }

    [Fact]
    public void WithNothingToCompareTheForecastSaysSo()
    {
        var copy = new MergeCopyView(new MergedCopy { ServerId = "s", ItemId = "i" }, "A", null, true);

        var forecast = MergeForecast.Of([new MergeTitleView("key", false, [], [copy])]);

        Assert.Null(forecast.Copy);
        Assert.False(forecast.IsCertain);
    }

    [Fact]
    public async Task APreviewChangesNothing()
    {
        await SyncChainAsync(TestItems.Source("a", 720, 1), TestItems.Source("b", 720, 2), TestItems.Source("c", 720, 3));

        Forecast("a", "b");

        Assert.Empty(_store.Merges);
    }

    [Fact]
    public async Task TheListAndTheForecastHoldNoAddressAndNoToken()
    {
        var hostile = new RemoteMediaSource
        {
            Id = "source",
            Name = "Version",
            Path = "http://" + TestValues.Host + ":8096/stream?api_key=" + TestValues.Token,
            MediaStreams = [new RemoteStreamInfo("Video", "h264", 1080, 1920, 1000, "SDR", "SDR", true)]
        };
        await SyncChainAsync(hostile, hostile with { Id = "other" }, TestItems.Source("c", 720, 1));

        var answer = JsonSerializer.Serialize(new object[] { Service().GetList(), Forecast("a", "b") });

        Assert.Contains("h264", answer, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Token, answer, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Host, answer, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Url, answer, StringComparison.Ordinal);
    }

    private sealed class LocalCatalog(IReadOnlyList<CatalogItem> items) : ILocalCatalogReader
    {
        public IReadOnlyList<CatalogItem> Read() => items;
    }
}
