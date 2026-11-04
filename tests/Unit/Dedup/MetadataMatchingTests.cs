using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;
using static Jellyfin.Plugin.JellyMesh.Tests.Dedup.Fixtures;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

/// <summary>
/// Decision 13: a film copy without IDs merges on normalized title and year only when the metadata confirm it. The rule
/// is written down in <c>MetadataComparison</c>, and a false merge is worse than a duplicate because it hides a title.
/// </summary>
public class MetadataMatchingTests
{
    private const long Minute = TimeSpan.TicksPerMinute;

    private static readonly SourceQuality _hd = Quality(720, 2000000);
    private static readonly SourceQuality _fullHd = Quality(1080, 4000000);
    private static readonly ContentIds _ids = new("1", "tt1");

    [Fact]
    public void TitleAndYearAlone_NeverMerge_AndAreReported()
    {
        var result = Resolve(
            Movie(Standard, "La La Land", 2016, null, _hd),
            Movie(RemoteJellyMesh, "Land", 2016, null, _fullHd));

        Assert.Equal(2, result.Decisions.Count);
        Assert.Equal(DedupIssueKind.PossibleDuplicate, Assert.Single(result.Issues).Kind);
    }

    [Fact]
    public void TitlesEqualOnlyWithoutArticles_StayApart_WhenTheRuntimeDiffers()
    {
        var result = Resolve(
            Movie(Standard, "La La Land", 2016, null, _hd).Lasting(128),
            Movie(RemoteJellyMesh, "Land", 2016, null, _fullHd).Lasting(89));

        Assert.Equal(2, result.Decisions.Count);
    }

    [Fact]
    public void TitlesEqualOnlyWithoutArticles_StayApart_WhenOriginalTitlesDiffer()
    {
        // The original title keeps its articles, or the two would be equal there too.
        var result = Resolve(
            With(Movie(Standard, "La La Land", 2016, null, _hd).Lasting(100), original: "La La Land"),
            With(Movie(RemoteJellyMesh, "Land", 2016, null, _fullHd).Lasting(100), original: "Land"));

        Assert.Equal(2, result.Decisions.Count);
    }

    [Fact]
    public void TitlesEqualOnlyWithoutArticles_Merge_WhenTheMetadataAgree()
    {
        var result = Resolve(
            With(Movie(Standard, "La La Land", 2016, null, _hd).Lasting(128), original: "La La Land"),
            With(Movie(RemoteJellyMesh, "Land", 2016, null, _fullHd).Lasting(128), original: "La La Land"));

        Assert.Single(result.Decisions);
    }

    [Fact]
    public void ACopyWithoutIds_MergesWithACopyWithIds()
    {
        var result = Resolve(
            Movie(Standard, "Film", 2020, _ids, _hd).Lasting(90),
            Movie(RemoteJellyMesh, "Film", 2020, null, _fullHd).Lasting(90));

        var decision = Assert.Single(result.Decisions);
        Assert.Equal("movie|tmdb:1", decision.Identity.Key);
        Assert.Equal(MatchKind.ProviderId, decision.Identity.Match);
        Assert.Equal(RemoteJellyMesh, decision.Winner?.ServerId);
        Assert.Equal(Standard, Assert.Single(decision.Backups).ServerId);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ACopyWithoutIds_MergesWithEveryCopyOfTheFilm()
    {
        var result = Resolve(
            Movie(Standard, "Film", 2020, new ContentIds("1"), _hd).Lasting(90),
            Movie(RemoteJellyMesh, "Film", 2020, new ContentIds("1", "tt1"), _hd).Lasting(90),
            Movie("server-c", "Film", 2020, null, _fullHd).Lasting(90));

        var decision = Assert.Single(result.Decisions);
        Assert.Equal("server-c", decision.Winner?.ServerId);
        Assert.Equal(2, decision.Backups.Count);
    }

    [Fact]
    public void ALocalCopyWithIds_HidesAConfirmedRemoteWithout()
    {
        var result = DedupEngine.Resolve(
            [Movie(Local, "Film", 2020, _ids).Lasting(90)],
            [Movie(Standard, "Film", 2020, null, _hd).Lasting(90)]);

        Assert.True(Assert.Single(result.Decisions).IsLocal);
    }

    [Fact]
    public void ALocalCopyWithoutIds_HidesAConfirmedRemoteWith()
    {
        var result = DedupEngine.Resolve(
            [Movie(Local, "Film", 2020, null).Lasting(90)],
            [Movie(Standard, "Film", 2020, _ids, _hd).Lasting(90)]);

        Assert.True(Assert.Single(result.Decisions).IsLocal);
    }

    [Fact]
    public void AnUnconfirmedLocalCopy_HidesNothing()
    {
        var result = DedupEngine.Resolve(
            [Movie(Local, "Film", 2020, null)],
            [Movie(Standard, "Film", 2020, null, _hd)]);

        Assert.False(Assert.Single(result.Decisions).IsLocal);
    }

    [Fact]
    public void ACopyThatCouldBeTwoFilms_GoesWhereTheMetadataPoint()
    {
        var result = Resolve(
            Movie(Standard, "Crash", 2004, new ContentIds("1"), _hd).Lasting(112),
            Movie(RemoteJellyMesh, "Crash", 2004, new ContentIds("2"), _hd).Lasting(90),
            Movie("server-c", "Crash", 2004, null, _fullHd).Lasting(90));

        Assert.Equal(2, result.Decisions.Count);
        Assert.Equal("server-c", result.Decisions.Single(decision => decision.Identity.Key == "movie|tmdb:2").Winner?.ServerId);
    }

    [Fact]
    public void ACopyThatCouldBeTwoFilms_StaysSeparate_WhenTheMetadataCannotTell()
    {
        var result = Resolve(
            Movie(Standard, "Crash", 2004, new ContentIds("1"), _hd).Lasting(90),
            Movie(RemoteJellyMesh, "Crash", 2004, new ContentIds("2"), _hd).Lasting(90),
            Movie("server-c", "Crash", 2004, null, _fullHd).Lasting(90));

        Assert.Equal(3, result.Decisions.Count);
        Assert.All(result.Decisions, decision => Assert.Empty(decision.Backups));
        Assert.Equal(3, Assert.Single(result.Issues).WorkKeys.Count);
    }

    [Fact]
    public void AContradictionWithOneCopyOfAFilm_OutweighsAConfirmationByAnother()
    {
        var result = Resolve(
            Movie(Standard, "Film", 2020, _ids, _hd).Lasting(90),
            Movie(RemoteJellyMesh, "Film", 2020, _ids, _hd).Lasting(120),
            Movie("server-c", "Film", 2020, null, _fullHd).Lasting(90));

        Assert.Equal(2, result.Decisions.Count);
    }

    [Fact]
    public void ACopyOfAFilmWithNoMetadata_DoesNotStopAConfirmationByAnother()
    {
        var result = Resolve(
            Movie(Standard, "Film", 2020, _ids, _hd).Lasting(90),
            Movie(RemoteJellyMesh, "Film", 2020, _ids, _hd),
            Movie("server-c", "Film", 2020, null, _fullHd).Lasting(90));

        Assert.Single(result.Decisions);
    }

    [Fact]
    public void CopiesOfAFilmUnderOtherTitles_DoNotCount()
    {
        // The Italian copy has an Italian plot, and it has nothing to do with the English title being matched.
        var result = Resolve(
            With(Movie(Standard, "The Godfather", 1972, _ids, _hd).Lasting(175), overview: "The aging patriarch of an organized crime dynasty hands over control"),
            With(Movie(RemoteJellyMesh, "Il Padrino", 1972, _ids, _hd).Lasting(175), overview: "Il vecchio capo di una famiglia mafiosa cede il controllo"),
            With(Movie("server-c", "Godfather", 1972, null, _fullHd).Lasting(175), overview: "The aging patriarch of an organized crime dynasty hands over control"));

        Assert.Single(result.Decisions);
    }

    [Fact]
    public void ATitleWithNeitherIdsNorYear_StaysAlone_WhateverItsMetadata()
    {
        var result = Resolve(
            Movie(Standard, "Film", null, null, _hd).Lasting(90),
            Movie(RemoteJellyMesh, "Film", null, null, _hd).Lasting(90),
            Movie("server-c", "Film", null, _ids, _hd).Lasting(90));

        Assert.Equal(3, result.Decisions.Count);
        Assert.Equal(2, result.Decisions.Count(decision => decision.Identity.Match == MatchKind.ItemOnly));
    }

    [Fact]
    public void CopiesWithoutIds_MergeOnlyIfEveryPairConfirms()
    {
        // The first and the last each agree with the middle one, on fields that never meet: nothing ties them to each other.
        var result = Resolve(
            With(Movie(Standard, "Film", 2020, null, _hd), original: "Film"),
            With(Movie(RemoteJellyMesh, "Film", 2020, null, _hd), original: "Film", people: ["Attore"]),
            With(Movie("server-c", "Film", 2020, null, _hd), people: ["Attore"]));

        Assert.Equal(3, result.Decisions.Count);
    }

    [Fact]
    public void ConfirmationIsNotTransitive()
    {
        // 1% of 100 minutes is one: the ends are two minutes apart, and each is one from the middle.
        var result = Resolve(
            Movie(Standard, "Film", 2020, null, _hd).Lasting(99),
            Movie(RemoteJellyMesh, "Film", 2020, null, _hd).Lasting(100),
            Movie("server-c", "Film", 2020, null, _hd).Lasting(101));

        Assert.Equal(3, result.Decisions.Count);
    }

    [Fact]
    public void ACopyConfirmingBothACopyAndAFilm_NeedsTheThirdPairToConfirm()
    {
        var result = Resolve(
            With(Movie(Standard, "Film", 2020, _ids, _hd).Lasting(90), original: "Film"),
            With(Movie(RemoteJellyMesh, "Film", 2020, null, _hd).Lasting(90), people: ["Attore"]),
            With(Movie("server-c", "Film", 2020, null, _hd), people: ["Attore"]));

        Assert.Equal(3, result.Decisions.Count);
    }

    [Fact]
    public void TwoGroupsWithTheSameTitle_GetDifferentKeys()
    {
        var result = Resolve(
            Movie(Standard, "Film", 2020, null, _hd).Lasting(90),
            Movie(RemoteJellyMesh, "Film", 2020, null, _hd).Lasting(90),
            Movie("server-c", "Film", 2020, null, _hd).Lasting(120),
            Movie("server-d", "Film", 2020, null, _hd).Lasting(120));

        Assert.Equal(
            ["movie|title:film|2020|remoto-jellymesh:item-Film", "movie|title:film|2020|server-c:item-Film"],
            result.Decisions.Select(decision => decision.Identity.Key));
    }

    [Theory]
    [InlineData(90 * Minute, 90 * Minute, 1)]
    [InlineData(90 * Minute, (90 * Minute) + (50 * TimeSpan.TicksPerSecond), 1)]
    [InlineData(90 * Minute, 92 * Minute, 2)]
    [InlineData(TimeSpan.TicksPerSecond * 60, TimeSpan.TicksPerSecond * 61, 1)]
    [InlineData(TimeSpan.TicksPerSecond * 60, TimeSpan.TicksPerSecond * 63, 2)]
    public void Runtimes_MustBeWithinTheTolerance(long left, long right, int decisions)
    {
        var result = Resolve(
            Movie(Standard, "Film", 2020, null, _hd) with { Metadata = new ComparableMetadata { RunTimeTicks = left } },
            Movie(RemoteJellyMesh, "Film", 2020, null, _hd) with { Metadata = new ComparableMetadata { RunTimeTicks = right } });

        Assert.Equal(decisions, result.Decisions.Count);
    }

    [Fact]
    public void GenresAndStudios_NeverConfirmOnTheirOwn()
    {
        var result = Resolve(
            With(Movie(Standard, "Film", 2020, null, _hd), genres: ["Drama"], studios: ["Studio"]),
            With(Movie(RemoteJellyMesh, "Film", 2020, null, _hd), genres: ["Drama"], studios: ["Studio"]));

        Assert.Equal(2, result.Decisions.Count);
    }

    [Theory]
    [InlineData("Drama", "Commedia")]
    [InlineData("Azione,Avventura,Fantascienza", "Azione,Drama,Thriller")]
    public void GenresThatDiffer_Contradict(string left, string right)
    {
        var result = Resolve(
            With(Movie(Standard, "Film", 2020, null, _hd).Lasting(90), genres: left.Split(',')),
            With(Movie(RemoteJellyMesh, "Film", 2020, null, _hd).Lasting(90), genres: right.Split(',')));

        Assert.Equal(2, result.Decisions.Count);
    }

    [Fact]
    public void StudiosThatDiffer_Contradict()
    {
        var result = Resolve(
            With(Movie(Standard, "Film", 2020, null, _hd).Lasting(90), studios: ["Alfa"]),
            With(Movie(RemoteJellyMesh, "Film", 2020, null, _hd).Lasting(90), studios: ["Beta"]));

        Assert.Equal(2, result.Decisions.Count);
    }

    [Fact]
    public void People_ComparedOnTheShorterList()
    {
        var result = Resolve(
            With(Movie(Standard, "Film", 2020, null, _hd), people: ["Renée Zellweger", "Mario Rossi"]),
            With(Movie(RemoteJellyMesh, "Film", 2020, null, _hd), people: ["RENEE ZELLWEGER", "Altro", "Altra", "Altri"]));

        Assert.Single(result.Decisions);
    }

    [Fact]
    public void PeopleThatDiffer_Contradict()
    {
        var result = Resolve(
            With(Movie(Standard, "Film", 2020, null, _hd).Lasting(90), people: ["Uno", "Due", "Tre"]),
            With(Movie(RemoteJellyMesh, "Film", 2020, null, _hd).Lasting(90), people: ["Quattro", "Cinque", "Tre"]));

        Assert.Equal(2, result.Decisions.Count);
    }

    [Fact]
    public void PlotsThatShareHalfTheirWords_Confirm()
    {
        var result = Resolve(
            With(Movie(Standard, "Film", 2020, null, _hd), overview: "A pianist and an actress fall in love in Los Angeles."),
            With(Movie(RemoteJellyMesh, "Film", 2020, null, _hd), overview: "A pianist and an actress fall in love while chasing dreams in Los Angeles."));

        Assert.Single(result.Decisions);
    }

    [Fact]
    public void PlotsInAnotherLanguage_LeaveADuplicate()
    {
        var result = Resolve(
            With(Movie(Standard, "Film", 2020, null, _hd).Lasting(90), overview: "A pianist and an actress fall in love in Los Angeles."),
            With(Movie(RemoteJellyMesh, "Film", 2020, null, _hd).Lasting(90), overview: "Un pianista e una attrice si innamorano a Los Angeles."));

        Assert.Equal(2, result.Decisions.Count);
    }

    [Fact]
    public void FieldsMissingOnOneSide_SayNothing()
    {
        var result = Resolve(
            With(Movie(Standard, "Film", 2020, null, _hd).Lasting(90), overview: "Una trama", genres: ["Drama"], people: ["Attore"]),
            Movie(RemoteJellyMesh, "Film", 2020, null, _hd).Lasting(90));

        Assert.Single(result.Decisions);
    }

    [Fact]
    public void PeopleAreNotReadOnceAFieldContradicts()
    {
        var people = new Lazy<IReadOnlyList<string>>(() => throw new InvalidOperationException("Read for nothing"));
        var left = Movie(Standard, "Film", 2020, null, _hd).Lasting(90);
        var right = Movie(RemoteJellyMesh, "Film", 2020, null, _hd).Lasting(120);

        var result = Resolve(left with { Metadata = left.Metadata with { People = people } }, right);

        Assert.Equal(2, result.Decisions.Count);
        Assert.False(people.IsValueCreated);
    }

    [Fact]
    public void PeopleAreNotReadWithoutACandidate()
    {
        var people = new Lazy<IReadOnlyList<string>>(() => throw new InvalidOperationException("Read for nothing"));
        var film = Movie(Local, "Film", 2020, _ids) with { Metadata = new ComparableMetadata { People = people } };

        DedupEngine.Resolve([film], [Movie(Standard, "Altro", 2020, null, _hd)]);

        Assert.False(people.IsValueCreated);
    }

    [Fact]
    public void AFederatedCopy_ConfirmsNothing()
    {
        // Decision 11: another server's stub is not a copy, so it must not pull a real copy into a merge.
        var result = DedupEngine.Resolve([], [
            Movie(Standard, "Film", 2020, _ids, _hd).Lasting(90) with { IsFederated = true },
            Movie(RemoteJellyMesh, "Film", 2020, null, _hd).Lasting(90),
        ]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(RemoteJellyMesh, decision.Winner?.ServerId);
        Assert.Equal(MatchKind.TitleYear, decision.Identity.Match);
    }

    [Fact]
    public void SeriesWithoutIds_StillMatchOnTitleAndYearAlone()
    {
        var result = Resolve(
            Episode(Standard, "La Serie", 2020, null, 1, 1, _hd),
            Episode(RemoteJellyMesh, "Serie", 2020, null, 1, 1, _fullHd));

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(MatchKind.TitleYear, decision.Identity.Match);
        Assert.Equal(RemoteJellyMesh, decision.Winner?.ServerId);
    }

    [Fact]
    public void SeriesWithoutIds_DoNotLinkToSeriesWithIds()
    {
        var result = Resolve(
            Episode(Standard, "Serie", 2020, null, 1, 1, _hd),
            Episode(RemoteJellyMesh, "Serie", 2020, new ContentIds("5"), 1, 1, _fullHd));

        Assert.Equal(2, result.Decisions.Count);
    }

    [Fact]
    public void Matching_DoesNotDependOnOrder()
    {
        CatalogItem[] items =
        [
            Movie(Standard, "Crash", 2004, new ContentIds("1"), _hd).Lasting(112),
            Movie(RemoteJellyMesh, "Crash", 2004, new ContentIds("2"), _hd).Lasting(90),
            Movie("server-c", "Crash", 2004, null, _fullHd).Lasting(90),
            Movie("server-d", "Crash", 2004, null, _hd).Lasting(90),
            Movie("server-e", "Crash", 2004, null, _hd).Lasting(60),
            Movie("server-f", "Crash", 2004, null, _hd).Lasting(60),
        ];

        var expected = Fingerprint(DedupEngine.Resolve([], items));
        foreach (var order in Permutations(items))
        {
            Assert.Equal(expected, Fingerprint(DedupEngine.Resolve([], order)));
        }
    }

    [Fact]
    public void Doubts_DoNotDependOnOrder()
    {
        CatalogItem[] items =
        [
            Movie(Standard, "Film", 2020, null, _hd).Lasting(99),
            Movie(RemoteJellyMesh, "Film", 2020, null, _hd).Lasting(100),
            Movie("server-c", "Film", 2020, null, _hd).Lasting(101),
            Movie("server-d", "Film", 2020, new ContentIds("1"), _hd).Lasting(100),
            Movie("server-e", "Film", 2020, new ContentIds("2"), _hd).Lasting(100),
        ];

        var expected = Fingerprint(DedupEngine.Resolve([], items));
        foreach (var order in Permutations(items))
        {
            Assert.Equal(expected, Fingerprint(DedupEngine.Resolve([], order)));
        }
    }

    private static DedupResult Resolve(params CatalogItem[] remote) => DedupEngine.Resolve([], remote);

    private static CatalogItem With(
        CatalogItem item,
        string? original = null,
        string? overview = null,
        string[]? genres = null,
        string[]? studios = null,
        string[]? people = null)
    {
        return item with
        {
            Metadata = item.Metadata with
            {
                OriginalTitle = original,
                Overview = overview,
                Genres = genres ?? [],
                Studios = studios ?? [],
                People = People(people ?? [])
            }
        };
    }
}
