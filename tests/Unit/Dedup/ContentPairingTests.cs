using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;
using static Jellyfin.Plugin.JellyMesh.Tests.Dedup.Fixtures;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

// Q412: the watch state of a removed server comes back on the same content, by the identity of §4.1.
public sealed class ContentPairingTests
{
    private const string Saved = "saved";
    private const string Current = "current";

    private static CatalogItem SeriesOf(string server, string title, int? year, ContentIds? ids) => new()
    {
        ServerId = server,
        ItemId = "series-" + title,
        Kind = ContentKind.Episode,
        Title = title,
        Year = year,
        Ids = ids ?? ContentIds.None
    };

    private static string[] Pairs(CatalogItem[] saved, CatalogItem[] current, CatalogItem[]? savedSeries = null, CatalogItem[]? currentSeries = null) =>
        DedupEngine.Pair(saved, savedSeries ?? [], current, currentSeries ?? [])
            .Select(pair => pair.Saved.ItemId + " > " + pair.Current.ItemId)
            .Order()
            .ToArray();

    [Fact]
    public void FilmsThatShareOneIdPair()
    {
        var saved = Movie(Saved, "Film", 2020, new ContentIds("1", "tt1"));
        var current = Movie(Current, "Altro nome", 2021, new ContentIds(Imdb: "tt1"));

        Assert.Equal(["item-Film > item-Altro nome"], Pairs([saved], [current]));
    }

    [Fact]
    public void FilmsWithoutIdsPairOnlyWhenTheMetadataConfirms()
    {
        var saved = Movie(Saved, "Sonda Senza ID", 2026, null).Lasting(90);

        Assert.Single(Pairs([saved], [Movie(Current, "Sonda Senza ID", 2026, null).Lasting(90)]));
        Assert.Empty(Pairs([saved], [Movie(Current, "Sonda Senza ID", 2026, null).Lasting(120)]));
        Assert.Empty(Pairs([saved], [Movie(Current, "Sonda Senza ID", 2026, null)]));
    }

    [Fact]
    public void AFilmWithoutIdsPairsWithACopyWithIdsThatItsMetadataConfirms()
    {
        var saved = Movie(Saved, "JM Copia Senza ID", 2026, null).Lasting(95);
        var current = Movie(Current, "JM Copia Senza ID", 2026, new ContentIds("77")).Lasting(95);

        Assert.Single(Pairs([saved], [current]));
    }

    [Fact]
    public void EpisodesOfASeriesWithoutIdsPairByTitleYearSeasonAndNumber()
    {
        var saved = Episode(Saved, "Serie Senza ID", 2026, null, 1, 2);

        Assert.Single(Pairs([saved], [Episode(Current, "Serie Senza ID", 2026, null, 1, 2)]));
        Assert.Empty(Pairs([saved], [Episode(Current, "Serie Senza ID", 2026, null, 1, 3)]));
        Assert.Empty(Pairs([saved], [Episode(Current, "Serie Senza ID", 2025, null, 1, 2)]));
    }

    [Fact]
    public void AFileWithSeveralEpisodesPairsWithEachOfThem()
    {
        var saved = Episode(Saved, "Show", 2020, new ContentIds(Tvdb: "9"), 1, 1) with { EpisodeNumberEnd = 2 };

        Assert.Equal(2, Pairs([saved], [Episode(Current, "Show", 2020, new ContentIds(Tvdb: "9"), 1, 1), Episode(Current, "Show", 2020, new ContentIds(Tvdb: "9"), 1, 2)]).Length);
    }

    [Fact]
    public void SeriesPairByTheirWork()
    {
        var withoutIds = SeriesOf(Saved, "Serie Senza ID", 2026, null);
        var withIds = SeriesOf(Saved, "JM Serie Divisa", 2026, new ContentIds("101", Tvdb: "102"));

        Assert.Equal(
            ["series-JM Serie Divisa > series-Divisa", "series-Serie Senza ID > series-Serie Senza ID"],
            Pairs([], [], [withoutIds, withIds], [SeriesOf(Current, "Serie Senza ID", 2026, null), SeriesOf(Current, "Divisa", 2026, new ContentIds(Tvdb: "102"))]));
    }

    [Fact]
    public void ASeriesWithoutIdsDoesNotPairWithOneWithIds()
    {
        Assert.Empty(Pairs([], [], [SeriesOf(Saved, "Show", 2020, null)], [SeriesOf(Current, "Show", 2020, new ContentIds(Tvdb: "1"))]));
    }

    [Fact]
    public void AFilmAndAnEpisodeNeverPair()
    {
        Assert.Empty(Pairs([Movie(Saved, "Show", 2020, new ContentIds("5"))], [Episode(Current, "Show", 2020, new ContentIds("5"), 1, 1)]));
    }

    [Fact]
    public void ACopyWithoutIdsOrYearPairsWithNothing()
    {
        Assert.Empty(Pairs([Movie(Saved, "Film", null, null).Lasting(90)], [Movie(Current, "Film", null, null).Lasting(90)]));
    }

    [Fact]
    public void ASavedCopyPairsWithEveryCurrentCopyOfItsContent()
    {
        var saved = Movie(Saved, "Film", 2020, new ContentIds("1"));
        var b = Movie("b", "Film", 2020, new ContentIds("1"));
        var c = Movie("c", "Film", 2020, new ContentIds("1")) with { ItemId = "other" };

        Assert.Equal(["item-Film > item-Film", "item-Film > other"], Pairs([saved], [b, c]));
    }
}
