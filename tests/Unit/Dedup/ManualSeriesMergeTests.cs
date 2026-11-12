using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;
using static Jellyfin.Plugin.JellyMesh.Tests.Dedup.Fixtures;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

/// <summary>
/// Decision 51: manual merges extend to series. A series copy is named by its series item, and a merge joins the works of
/// whole series, which are then completed per episode as any series (§4.2 rule 4).
/// </summary>
public class ManualSeriesMergeTests
{
    private const string Third = "terzo";

    private static readonly SourceQuality _sd = Quality(480, 1000000);
    private static readonly SourceQuality _hd = Quality(720, 2000000);
    private static readonly SourceQuality _fullHd = Quality(1080, 4000000);

    private const string KeyOne = "series|tmdb:1";
    private const string KeyTwo = "series|tmdb:2";

    private static readonly CatalogItem _oneE1 = Episode(Standard, "Serie", 2020, new ContentIds("1"), 1, 1, _hd);
    private static readonly CatalogItem _oneE2 = Episode(Standard, "Serie", 2020, new ContentIds("1"), 1, 2, _hd);
    private static readonly CatalogItem _twoE1 = Episode(RemoteJellyMesh, "Serie", 2020, new ContentIds("2"), 1, 1, _fullHd) with { SeriesId = "series-due" };
    private static readonly CatalogItem _twoE3 = Episode(RemoteJellyMesh, "Serie", 2020, new ContentIds("2"), 1, 3, _fullHd) with { SeriesId = "series-due" };

    private static Guid Id(int number) => new(number, 0, 0, new byte[8]);

    private static ManualMerge Merge(int id, params CatalogItem[] items) => new()
    {
        Id = Id(id),
        CreatedUtc = DateTimeOffset.UnixEpoch,
        Copies = items.Select(item => MergedCopy.From(item, item.ServerId == Local)).ToList()
    };

    private static DedupResult Resolve(IEnumerable<CatalogItem> remote, params ManualMerge[] merges) =>
        DedupEngine.Resolve([], remote, merges);

    private static string Fingerprint(DedupResult result)
    {
        var cases = result.MergeCases.Select(entry => string.Join(
            " / ",
            entry.Works.Select(work => work.WorkKey + "=" + string.Join("+", work.Copies.Select(copy => copy.Item.ServerId + ":" + copy.Item.ItemId)))));
        var merges = result.Merges.Select(merge => $"{merge.Merge.Id} {merge.WorkKey} {merge.IsInEffect} {string.Join("+", merge.Copies.Select(copy => copy.Item?.ItemId))}");
        var items = result.Decisions.Select(decision => decision.Identity.Key + " " + string.Join("+", decision.RemoteItems));
        return string.Join('\n', new[] { Fixtures.Fingerprint(result) }.Concat(cases).Concat(merges).Concat(items));
    }

    [Fact]
    public void ACopyOfASeries_IsNamedByItsSeriesItem()
    {
        var copy = MergedCopy.From(_oneE1 with { Ids = new ContentIds(" 1 ", "TT1", "77") }, false);

        Assert.Equal(ContentKind.Episode, copy.Kind);
        Assert.Equal("series-Serie", copy.ItemId);
        Assert.Equal(("1", "tt1", "77"), (copy.Tmdb, copy.Imdb, copy.Tvdb));
        Assert.Equal("Serie", copy.Title);
    }

    [Fact]
    public void AFilmCopy_StaysAFilm_AndKeepsNoTvdb()
    {
        var copy = MergedCopy.From(Movie(Standard, "Film", 2020, new ContentIds("1", null, "77"), _hd), false);

        Assert.Equal(ContentKind.Movie, copy.Kind);
        Assert.Null(copy.Tvdb);
    }

    [Fact]
    public void SeriesKeptApart_AreACase_WithOneCopyPerSeries()
    {
        var result = Resolve([_oneE1, _oneE2, _twoE1, _twoE3]);

        var group = Assert.Single(result.MergeCases);
        Assert.Equal(DedupIssueKind.PossibleDuplicate, group.Kind);
        Assert.Equal(ContentKind.Episode, group.ContentKind);
        Assert.Equal([KeyOne, KeyTwo], group.Works.Select(work => work.WorkKey));
        Assert.Equal(["series-Serie"], group.Works[0].Copies.Select(copy => copy.Item.SeriesId));
        Assert.Equal(["series-due"], group.Works[1].Copies.Select(copy => copy.Item.SeriesId));
    }

    [Fact]
    public void AFilmCase_SaysItIsOfFilms()
    {
        var first = Movie(Standard, "Doppio", 2020, new ContentIds("1"), _hd);
        var second = Movie(RemoteJellyMesh, "Doppio", 2020, new ContentIds("2"), _hd);

        Assert.Equal(ContentKind.Movie, Assert.Single(Resolve([first, second]).MergeCases).ContentKind);
    }

    [Fact]
    public void MergedSeries_AreOneWork_CompletedPerEpisode_TheBestCopyOfEachEpisodeWins()
    {
        var result = Resolve([_oneE1, _oneE2, _twoE1, _twoE3], Merge(1, _oneE1, _twoE1));

        Assert.Equal([KeyOne + "|S1E1", KeyOne + "|S1E2", KeyOne + "|S1E3"], result.Decisions.Select(decision => decision.Identity.Key));
        Assert.All(result.Decisions, decision => Assert.Equal(MatchKind.Manual, decision.Identity.Match));
        Assert.All(result.Decisions, decision => Assert.Equal(KeyOne, decision.Identity.WorkKey));
        var first = result.Decisions[0];
        Assert.Equal(1080, first.Winner!.Quality.Height);
        Assert.Equal([720], first.Backups.Select(backup => backup.Quality.Height!.Value));
        Assert.Empty(result.MergeCases);
        Assert.True(Assert.Single(result.Merges).IsInEffect);
        Assert.Equal(KeyOne, result.Merges[0].WorkKey);
    }

    [Fact]
    public void AMergedSeries_FindsEachCopyByItsSeries_WhicheverEpisodeNamedIt()
    {
        var merge = Merge(1, _oneE2, _twoE3);

        var result = Resolve([_oneE1, _twoE1], merge);

        Assert.Equal([KeyOne + "|S1E1"], result.Decisions.Select(decision => decision.Identity.Key));
        Assert.All(Assert.Single(result.Merges).Copies, copy => Assert.NotNull(copy.Item));
    }

    [Fact]
    public void ASeriesCopyThatGainsIds_StaysInTheMerge()
    {
        var merge = Merge(1, _oneE1, _twoE1);
        var corrected = new ContentIds("9", null, "99");

        var result = Resolve([_oneE1, _twoE1 with { Ids = corrected }, _twoE3 with { Ids = corrected }], merge);

        Assert.Equal(2, result.Decisions.Count);
        Assert.All(result.Decisions, decision => Assert.Equal(MatchKind.Manual, decision.Identity.Match));
    }

    [Fact]
    public void ASeriesCopyThatLosesItsYearAndIds_StaysInTheMerge()
    {
        var merge = Merge(1, _oneE1, _twoE1);

        var result = Resolve([_oneE1, _twoE1 with { Year = null, Ids = ContentIds.None }, _twoE3 with { Year = null, Ids = ContentIds.None }], merge);

        // The joined works take the lowest of their keys, here the key of the copy alone.
        Assert.Equal(["S1E1", "S1E3"], result.Decisions.Select(decision => decision.Identity.Key.Split('|')[^1]));
        Assert.Single(result.Decisions.Select(decision => decision.Identity.Key[..decision.Identity.Key.LastIndexOf('|')]).Distinct());
        Assert.All(result.Decisions, decision => Assert.Equal(MatchKind.Manual, decision.Identity.Match));
        Assert.Equal(2, result.Decisions[0].RemoteItems.Count);
    }

    [Fact]
    public void ASeriesCopyThatMoved_IsFoundByItsIds_TvdbIncluded()
    {
        var ids = new ContentIds("2", null, "22");
        var two = _twoE1 with { Ids = ids };
        var merge = Merge(1, _oneE1, two);

        var result = Resolve([_oneE1, two with { SeriesId = "series-nuova", ItemId = "nuovo" }], merge);

        Assert.Single(result.Decisions);
        Assert.Equal("nuovo", Assert.Single(result.Merges).Copies.Single(copy => copy.Copy.ServerId == RemoteJellyMesh).Item!.ItemId);
    }

    [Fact]
    public void ASeriesCopy_NeverFindsAFilmWithTheSameIds()
    {
        // TMDb numbers films and series apart, so the same number names two different titles.
        var film = Movie(Third, "Serie", 2020, new ContentIds("2"), _sd);
        var merge = Merge(1, _oneE1, _twoE1);

        var result = Resolve([_oneE1, film, _twoE1 with { SeriesId = "series-nuova", Ids = new ContentIds("5") }], merge);

        var applied = Assert.Single(result.Merges);
        Assert.Equal(ContentKind.Episode, applied.Copies.Single(copy => copy.Copy.ServerId == RemoteJellyMesh).Copy.Kind);
        Assert.Null(applied.Copies.Single(copy => copy.Copy.ServerId == RemoteJellyMesh).Item);
        Assert.Equal("movie|tmdb:2", result.Decisions.Single(decision => decision.Identity.Kind == ContentKind.Movie).Identity.Key);
    }

    [Fact]
    public void AFilmCopy_NeverFindsASeriesWithTheSameIds()
    {
        var film = Movie(Third, "Film", 2020, new ContentIds("1"), _sd);
        var other = Movie(Standard, "Film", 2020, new ContentIds("3"), _sd);
        var merge = Merge(1, film with { ItemId = "sparito" }, other);

        var result = Resolve([_oneE1, other], merge);

        Assert.Equal(KeyOne + "|S1E1", result.Decisions.Single(decision => decision.Identity.Kind == ContentKind.Episode).Identity.Key);
        Assert.False(Assert.Single(result.Merges).IsInEffect);
    }

    [Fact]
    public void AMergeOfAFilmAndASeries_JoinsNothingAcrossKinds()
    {
        var film = Movie(Third, "Film", 2020, new ContentIds("5"), _sd);

        var result = Resolve([_oneE1, film], Merge(1, _oneE1, film));

        Assert.Equal(["movie|tmdb:5", KeyOne + "|S1E1"], result.Decisions.Select(decision => decision.Identity.Key));
        Assert.False(Assert.Single(result.Merges).IsInEffect);
    }

    [Fact]
    public void AnEpisodeWithoutItsSeries_IsNoCopy_ButComesAlongWithItsWork()
    {
        var orphan = _twoE3 with { SeriesId = null, ItemId = "orfano" };

        var result = Resolve([_oneE1, _twoE1, orphan]);

        var group = Assert.Single(result.MergeCases);
        Assert.Single(group.Works[1].Copies);

        var merged = Resolve([_oneE1, _twoE1, orphan], Merge(1, _oneE1, _twoE1));
        Assert.Equal([KeyOne + "|S1E1", KeyOne + "|S1E3"], merged.Decisions.Select(decision => decision.Identity.Key));
    }

    [Fact]
    public void ATitleWhoseOnlyEpisodesHaveNoSeries_LeavesTheCase()
    {
        var orphan = _twoE1 with { SeriesId = null };

        Assert.Empty(Resolve([_oneE1, orphan]).MergeCases);
    }

    [Fact]
    public void ALocalSeriesAndARemoteOne_AreACase_AndTheLocalEpisodesStillWin()
    {
        var local = Episode(Local, "Serie", 2020, new ContentIds("1"), 1, 1) with { ItemId = "locale-e1", SeriesId = "locale-serie" };

        var apart = DedupEngine.Resolve([local], [_twoE1, _twoE3]);
        var group = Assert.Single(apart.MergeCases);
        Assert.Equal([true, false], group.Works.Select(work => work.HasLocalCopy));

        var merged = DedupEngine.Resolve([local], [_twoE1, _twoE3], [Merge(1, local, _twoE1)]);

        Assert.True(merged.Decisions.Single(decision => decision.Identity.EpisodeNumber == 1).IsLocal);
        Assert.False(merged.Decisions.Single(decision => decision.Identity.EpisodeNumber == 3).IsLocal);
        Assert.Equal("locale-e1", merged.LocalItemByWork[KeyOne]);
    }

    [Fact]
    public void TwoLocalSeries_AreNoCase()
    {
        var first = Episode(Local, "Serie", 2020, new ContentIds("1"), 1, 1) with { SeriesId = "locale-1" };
        var second = Episode(Local, "Serie", 2020, new ContentIds("2"), 1, 1) with { ItemId = "altro", SeriesId = "locale-2" };

        Assert.Empty(DedupEngine.Resolve([first, second], []).MergeCases);
    }

    [Fact]
    public void AFileWithSeveralEpisodes_JoinsEachOfThem()
    {
        var both = _twoE1 with { EpisodeNumberEnd = 2 };

        var result = Resolve([_oneE1, _oneE2, both], Merge(1, _oneE1, both));

        Assert.Equal([KeyOne + "|S1E1", KeyOne + "|S1E2"], result.Decisions.Select(decision => decision.Identity.Key));
        Assert.All(result.Decisions, decision => Assert.Equal(1080, decision.Winner!.Quality.Height));
    }

    [Fact]
    public void AnUndo_GivesTheSameResultAsNeverHavingMerged()
    {
        Assert.Equal(Fingerprint(Resolve([_oneE1, _twoE1])), Fingerprint(DedupEngine.Resolve([], [_oneE1, _twoE1], [])));
    }

    [Fact]
    public void TheResultDoesNotDependOnTheOrderOfItemsCopiesOrMerges()
    {
        var third = Episode(Third, "Serie", 2020, new ContentIds("3"), 1, 1, _sd) with { SeriesId = "series-tre" };
        var catalog = new[] { _oneE1, _oneE2, _twoE1, _twoE3, third };
        var merges = new[] { Merge(2, _twoE3, third), Merge(1, _oneE2, _twoE1) };
        var expected = Fingerprint(Resolve(catalog, merges));

        var random = new Random(51);
        for (var turn = 0; turn < 200; turn++)
        {
            var shuffled = Shuffled(merges, random).Select(merge => merge with { Copies = Shuffled(merge.Copies, random) }).ToArray();
            Assert.Equal(expected, Fingerprint(Resolve(Shuffled(catalog, random), shuffled)));
        }
    }
}
