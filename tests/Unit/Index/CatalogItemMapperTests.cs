using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Index;

public class CatalogItemMapperTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 4, 3, 0, 0, TimeSpan.Zero);
    private static readonly Guid _serverId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static IndexedItem Map(RemoteItem item, RemoteItemType type)
        => RemoteItemMapper.ToIndexedItem(item, type, "library", _now);

    private static ServerCatalog Catalog(params IndexedItem[] items)
        => new() { ServerId = _serverId, State = ServerSyncState.Complete, Items = items };

    [Fact]
    public void AnEpisodeTakesTitleYearAndIdsFromItsSeries()
    {
        var series = Map(TestItems.Series("s", "Show", 2019), RemoteItemType.Series);
        var episode = Map(TestItems.Episode("e", "s", 2, 3, "Other name"), RemoteItemType.Episode);

        var item = Assert.Single(CatalogItemMapper.ToCatalogItems(Catalog(series, episode)));

        Assert.Equal(ContentKind.Episode, item.Kind);
        Assert.Equal("Show", item.Title);
        Assert.Equal(2019, item.Year);
        Assert.Equal(new ContentIds("72", "tt71", "71"), item.Ids);
        Assert.Equal((2, 3), (item.SeasonNumber, item.EpisodeNumber));
        Assert.Equal(_serverId.ToString("N"), item.ServerId);
    }

    [Fact]
    public void AnEpisodeWithoutItsSeriesKeepsTheSeriesNameAndNoYear()
    {
        var episode = Map(TestItems.Episode("e", "gone", 1, 1), RemoteItemType.Episode);

        var item = Assert.Single(CatalogItemMapper.ToCatalogItems(Catalog(episode)));

        Assert.Equal("Show", item.Title);
        Assert.Null(item.Year);
        Assert.Equal(ContentIds.None, item.Ids);
    }

    [Fact]
    public void SeriesAndSeasonsAreNotItemsOfTheirOwn()
    {
        var items = CatalogItemMapper.ToCatalogItems(Catalog(
            Map(TestItems.Series("s"), RemoteItemType.Series),
            Map(TestItems.Season("s1", "s", 1), RemoteItemType.Season)));

        Assert.Empty(items);
    }

    [Fact]
    public void EachMediaSourceIsOneVersion()
    {
        var movie = TestItems.Movie("m") with
        {
            MediaSources = [TestItems.Source("a", 1080, 5_000_000), TestItems.Source("b", 720, 2_000_000)]
        };

        var item = Assert.Single(CatalogItemMapper.ToCatalogItems(Catalog(Map(movie, RemoteItemType.Movie))));

        Assert.Equal(["a", "b"], item.Versions.Select(version => version.MediaSourceId));
        Assert.Equal(1080, item.Versions[0].Quality.Height);
        Assert.Equal(720, item.Versions[1].Quality.Height);
    }

    [Fact]
    public void QualityComesFromTheVideoStreamUncorrected()
    {
        var movie = TestItems.Movie("m") with { MediaSources = [TestItems.Source("a", 1080, 2_650_000, "av1", "HDR")] };

        var quality = CatalogItemMapper.ToCatalogItems(Catalog(Map(movie, RemoteItemType.Movie)))[0].Versions[0].Quality;

        Assert.Equal(new SourceQuality(1080, true, 2_650_000, "av1"), quality);
    }

    [Theory]
    [InlineData("SDR", false)]
    [InlineData("Unknown", false)]
    [InlineData("HDR", true)]
    public void OnlyAnHdrRangeCountsAsHdr(string range, bool expected)
    {
        var movie = TestItems.Movie("m") with { MediaSources = [TestItems.Source("a", 1080, 1000, "hevc", range)] };

        Assert.Equal(expected, CatalogItemMapper.ToCatalogItems(Catalog(Map(movie, RemoteItemType.Movie)))[0].Versions[0].Quality.IsHdr);
    }

    [Fact]
    public void ASourceWithoutVideoHasNoQualityValues()
    {
        var movie = TestItems.Movie("m") with { MediaSources = [new RemoteMediaSource { Id = "strm", Container = "strm", IsRemote = true, Protocol = "Http" }] };

        var indexed = Map(movie, RemoteItemType.Movie);

        Assert.True(indexed.Versions[0].IsRemote);
        Assert.Equal(new SourceQuality(null, false, null, null), CatalogItemMapper.ToCatalogItems(Catalog(indexed))[0].Versions[0].Quality);
    }

    [Fact]
    public void AllProviderIdsAreKeptAndEmptyOnesDropped()
    {
        var series = TestItems.Series("s") with
        {
            ProviderIds = new System.Collections.Generic.Dictionary<string, string?>
            {
                ["Tvdb"] = "1",
                ["Custom"] = "2",
                ["Anything"] = "3",
                ["Empty"] = string.Empty,
                ["Null"] = null
            }
        };

        var ids = Map(series, RemoteItemType.Series).ProviderIds;

        Assert.Equal(3, ids.Count);
        Assert.Equal("2", ids["Custom"]);
        Assert.Equal("3", ids["Anything"]);
    }

    [Fact]
    public void TheFieldsOfAFullNfoAreCopied()
    {
        var movie = new RemoteItem
        {
            Id = "m",
            Overview = "Plot",
            Genres = ["Drama"],
            People = [new RemotePerson("Name", "Role", "Actor")],
            Studios = [new RemoteNamedItem("Studio"), new RemoteNamedItem(null)],
            OfficialRating = "PG",
            CommunityRating = 7.5f,
            CriticRating = 80f,
            RunTimeTicks = 600,
            Etag = "etag",
            DateCreated = _now,
            ImageTags = new System.Collections.Generic.Dictionary<string, string> { ["Primary"] = "tag" },
            BackdropImageTags = ["backdrop"]
        };

        var item = Map(movie, RemoteItemType.Movie);

        Assert.Equal("Plot", item.Overview);
        Assert.Equal(["Drama"], item.Genres);
        Assert.Equal(new IndexedPerson("Name", "Role", "Actor"), Assert.Single(item.People));
        Assert.Equal(["Studio"], item.Studios);
        Assert.Equal(("PG", 7.5f, 80f), (item.OfficialRating, item.CommunityRating, item.CriticRating));
        Assert.Equal(600, item.RunTimeTicks);
        Assert.Equal(("etag", (DateTimeOffset?)_now), (item.Etag, item.DateCreated));
        Assert.Equal("tag", item.ImageTags["Primary"]);
        Assert.Equal(["backdrop"], item.BackdropImageTags);
    }
}
