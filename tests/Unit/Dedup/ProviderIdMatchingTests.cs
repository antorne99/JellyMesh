using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;
using static Jellyfin.Plugin.JellyMesh.Tests.Dedup.Fixtures;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

/// <summary>
/// Decision 13: one matching ID is enough, even when the others differ. These tests also pin what the plan leaves to
/// <c>WorkResolver</c>: a chain of shared IDs merges unless it joins two copies that contradict each other, and anything
/// less certain stays separate and is reported, because a duplicate can be seen and a hidden title cannot.
/// </summary>
public class ProviderIdMatchingTests
{
    private static readonly SourceQuality _hd = Quality(720, 2000000);
    private static readonly SourceQuality _fullHd = Quality(1080, 4000000);

    [Fact]
    public void SameTmdb_Merges_EvenWithDifferentTitles()
    {
        var result = Resolve(
            Movie(Standard, "Sintel", 2010, new ContentIds("45745"), _hd),
            Movie(RemoteJellyMesh, "Sintel - Il Film", 2011, new ContentIds("45745"), _fullHd));

        var decision = Assert.Single(result.Decisions);
        Assert.Equal("movie|tmdb:45745", decision.Identity.Key);
        Assert.Equal(MatchKind.ProviderId, decision.Identity.Match);
    }

    [Fact]
    public void SameImdb_Merges()
    {
        var result = Resolve(
            Movie(Standard, "Sintel", 2010, new ContentIds(Imdb: "tt1727587"), _hd),
            Movie(RemoteJellyMesh, "Sintel", 2010, new ContentIds(Imdb: "tt1727587"), _fullHd));

        Assert.Equal("movie|imdb:tt1727587", Assert.Single(result.Decisions).Identity.Key);
    }

    [Fact]
    public void OneSharedId_Merges_WhenTheOtherIsMissingOnOneSide()
    {
        var result = Resolve(
            Movie(Standard, "Sintel", 2010, new ContentIds("45745", "tt1727587"), _hd),
            Movie(RemoteJellyMesh, "Sintel", 2010, new ContentIds(Imdb: "tt1727587"), _fullHd));

        var decision = Assert.Single(result.Decisions);
        Assert.Equal("movie|tmdb:45745", decision.Identity.Key);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void LocalWins_ThroughAnyOneSharedId()
    {
        var result = DedupEngine.Resolve(
            [Movie(Local, "Sintel", 2010, new ContentIds("45745", "tt1727587"))],
            [Movie(RemoteJellyMesh, "Sintel", 2010, new ContentIds(Imdb: "tt1727587"), _fullHd)]);

        Assert.True(Assert.Single(result.Decisions).IsLocal);
    }

    [Fact]
    public void TmdbOnOne_ImdbOnAnother_StaySeparate_AndAreReported()
    {
        var result = Resolve(
            Movie(Standard, "Sintel", 2010, new ContentIds("45745"), _hd),
            Movie(RemoteJellyMesh, "Sintel", 2010, new ContentIds(Imdb: "tt1727587"), _fullHd));

        Assert.Equal(2, result.Decisions.Count);
        Assert.All(result.Decisions, decision => Assert.NotNull(decision.Winner));
        var issue = Assert.Single(result.Issues);
        Assert.Equal(DedupIssueKind.PossibleDuplicate, issue.Kind);
        Assert.Equal(["movie|imdb:tt1727587", "movie|tmdb:45745"], issue.WorkKeys);
    }

    [Fact]
    public void TmdbOnOne_ImdbOnAnother_MergeWhenAThirdItemHasBoth()
    {
        var result = Resolve(
            Movie(Standard, "Sintel", 2010, new ContentIds("45745"), _hd),
            Movie(RemoteJellyMesh, "Sintel", 2010, new ContentIds(Imdb: "tt1727587"), _fullHd),
            Movie("server-c", "Sintel", 2010, new ContentIds("45745", "tt1727587"), _hd));

        Assert.Equal(2, Assert.Single(result.Decisions).Backups.Count);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void IdOnOne_NoneOnAnother_StaySeparate_AndAreReported()
    {
        var result = Resolve(
            Movie(Standard, "Sintel", 2010, new ContentIds("45745"), _hd),
            Movie(RemoteJellyMesh, "Sintel", 2010, null, _fullHd));

        Assert.Equal(2, result.Decisions.Count);
        Assert.Equal(DedupIssueKind.PossibleDuplicate, Assert.Single(result.Issues).Kind);
    }

    [Fact]
    public void LocalWithId_DoesNotHideARemoteWithoutId()
    {
        var result = DedupEngine.Resolve(
            [Movie(Local, "Big Buck Bunny", 2008, new ContentIds("10378"))],
            [Movie(Standard, "Big Buck Bunny (2008)", 2008, null, _hd)]);

        var decision = Assert.Single(result.Decisions);
        Assert.False(decision.IsLocal);
        Assert.Equal(MatchKind.TitleYear, decision.Identity.Match);
        Assert.Equal(
            ["movie|title:big buck bunny|2008", "movie|tmdb:10378"],
            Assert.Single(result.Issues).WorkKeys);
    }

    [Fact]
    public void LocalWithoutId_DoesNotHideARemoteWithId()
    {
        var result = DedupEngine.Resolve(
            [Movie(Local, "Big Buck Bunny", 2008, null)],
            [Movie(Standard, "Big Buck Bunny", 2008, new ContentIds("10378"), _hd)]);

        Assert.False(Assert.Single(result.Decisions).IsLocal);
    }

    [Fact]
    public void SameTitleAndYear_WithDifferentIds_StaySeparate()
    {
        var result = Resolve(
            Movie(Standard, "Titolo", 2020, new ContentIds("1"), _hd),
            Movie(RemoteJellyMesh, "Titolo", 2020, new ContentIds("2"), _hd));

        Assert.Equal(2, result.Decisions.Count);
    }

    [Fact]
    public void OneSharedId_Merges_EvenWhenTheOtherIdDiffers()
    {
        var result = Resolve(
            Movie(Standard, "Sintel", 2010, new ContentIds("45745", "tt1"), _hd),
            Movie(RemoteJellyMesh, "Sintel", 2010, new ContentIds("45745", "tt2"), _fullHd));

        var decision = Assert.Single(result.Decisions);
        Assert.Equal("movie|tmdb:45745", decision.Identity.Key);
        Assert.Equal(RemoteJellyMesh, decision.Winner?.ServerId);
        Assert.Equal(Standard, Assert.Single(decision.Backups).ServerId);
    }

    [Fact]
    public void AMergeOnPartlyDifferingIds_IsReported()
    {
        var result = Resolve(
            Movie(Standard, "Sintel", 2010, new ContentIds("45745", "tt1"), _hd),
            Movie(RemoteJellyMesh, "Sintel", 2010, new ContentIds("45745", "tt2"), _fullHd));

        var issue = Assert.Single(result.Issues);
        Assert.Equal(DedupIssueKind.ConflictingProviderIds, issue.Kind);
        Assert.Equal(["movie|tmdb:45745"], issue.WorkKeys);
    }

    [Fact]
    public void OneSharedId_LocalWins_EvenWhenTheOtherIdDiffers()
    {
        var result = DedupEngine.Resolve(
            [Movie(Local, "Sintel", 2010, new ContentIds("45745", "tt1"))],
            [Movie(Standard, "Sintel", 2010, new ContentIds("45745", "tt2"), _hd)]);

        Assert.True(Assert.Single(result.Decisions).IsLocal);
    }

    [Fact]
    public void AllCopiesThatShareOneId_Merge_WhateverTheirOtherIds()
    {
        var result = Resolve(
            Movie(Standard, "Sintel", 2010, new ContentIds("45745"), _hd),
            Movie(RemoteJellyMesh, "Sintel", 2010, new ContentIds("45745", "tt1"), _hd),
            Movie("server-c", "Sintel", 2010, new ContentIds("45745", "tt2"), _hd),
            Movie("server-d", "Sintel", 2010, new ContentIds("45745", "tt2"), _fullHd));

        var decision = Assert.Single(result.Decisions);
        Assert.Equal("server-d", decision.Winner?.ServerId);
        Assert.Equal(3, decision.Backups.Count);
    }

    [Fact]
    public void TheKey_IsTheLowestValue_WhenTheMergedCopiesDiffer()
    {
        CatalogItem[] items =
        [
            Movie(Standard, "Sintel", 2010, new ContentIds("20", "tt1"), _hd),
            Movie(RemoteJellyMesh, "Sintel", 2010, new ContentIds("3", "tt1"), _hd),
        ];

        foreach (var order in Permutations(items))
        {
            Assert.Equal("movie|tmdb:20", Assert.Single(DedupEngine.Resolve([], order).Decisions).Identity.Key);
        }
    }

    [Fact]
    public void AThirdCopy_JoinsTwoCopiesThatDoNotContradictEachOther()
    {
        // The first two have different IDs but no provider in common, so nothing says they are different.
        var result = Resolve(
            Movie(Standard, "Sintel", 2010, new ContentIds("45745"), _hd),
            Movie(RemoteJellyMesh, "Sintel", 2010, new ContentIds(Imdb: "tt1"), _hd),
            Movie("server-c", "Sintel", 2010, new ContentIds("45745", "tt1"), _hd));

        Assert.Single(result.Decisions);
    }

    [Fact]
    public void AChain_ThatJoinsTwoContradictingCopies_StaysSeparate_AndIsReported()
    {
        // The ends share no ID and disagree on both providers, so one of the links is wrong and nobody can tell which.
        var result = Resolve(
            Movie(Standard, "Sintel", 2010, new ContentIds("1", "tt1"), _hd),
            Movie(RemoteJellyMesh, "Sintel", 2010, new ContentIds("1", "tt2"), _hd),
            Movie("server-c", "Sintel", 2010, new ContentIds("2", "tt2"), _hd));

        Assert.Equal(
            ["movie|tmdb:1|imdb:tt1", "movie|tmdb:1|imdb:tt2", "movie|tmdb:2|imdb:tt2"],
            result.Decisions.Select(decision => decision.Identity.Key));
        Assert.Equal(
            ["movie|tmdb:1|imdb:tt1", "movie|tmdb:1|imdb:tt2", "movie|tmdb:2|imdb:tt2"],
            result.Issues.Single(issue => issue.Kind == DedupIssueKind.ConflictingProviderIds).WorkKeys);
    }

    [Fact]
    public void AChain_WithAContradiction_LeavesALocalCopyOnlyItsOwnSetOfIds()
    {
        var result = DedupEngine.Resolve(
            [Movie(Local, "Sintel", 2010, new ContentIds("1", "tt1"))],
            [
                Movie(Standard, "Sintel", 2010, new ContentIds("1", "tt1"), _hd),
                Movie(RemoteJellyMesh, "Sintel", 2010, new ContentIds("1", "tt2"), _hd),
                Movie("server-c", "Sintel", 2010, new ContentIds("2", "tt2"), _hd),
            ]);

        Assert.Equal(3, result.Decisions.Count);
        Assert.Single(result.Decisions, decision => decision.IsLocal);
    }

    [Fact]
    public void Chains_DoNotDependOnOrder()
    {
        CatalogItem[] items =
        [
            Movie(Standard, "Sintel", 2010, new ContentIds("1", "tt1"), _hd),
            Movie(RemoteJellyMesh, "Sintel", 2010, new ContentIds("1", "tt2"), _hd),
            Movie("server-c", "Sintel", 2010, new ContentIds("2", "tt2"), _hd),
            Movie("server-d", "Altro", 2011, new ContentIds("7", "tt7"), _hd),
            Movie("server-e", "Altro", 2011, new ContentIds("8", "tt7"), _hd),
        ];

        var expected = Fingerprint(DedupEngine.Resolve([], items));
        foreach (var order in Permutations(items))
        {
            Assert.Equal(expected, Fingerprint(DedupEngine.Resolve([], order)));
        }
    }

    [Fact]
    public void PartlyDifferingIds_InASeries_ApplyToItsEpisodes()
    {
        var result = Resolve(
            Episode(Standard, "Serie", 2020, new ContentIds("100", Tvdb: "1"), 1, 1, _hd),
            Episode(RemoteJellyMesh, "Serie", 2020, new ContentIds("100", Tvdb: "2"), 1, 1, _fullHd));

        Assert.Single(result.Decisions);
        Assert.Contains(result.Issues, issue => issue.Kind == DedupIssueKind.ConflictingProviderIds);
    }

    [Fact]
    public void IdsOfFilmsAndSeries_AreDifferentNamespaces()
    {
        var result = Resolve(
            Movie(Standard, "Film", 2020, new ContentIds("100"), _hd),
            Episode(RemoteJellyMesh, "Serie", 2020, new ContentIds("100"), 1, 1, _hd));

        Assert.Equal(2, result.Decisions.Count);
    }

    [Fact]
    public void Tvdb_CountsOnSeriesButNotOnFilms()
    {
        var result = Resolve(
            Movie(Standard, "Primo", 2020, new ContentIds(Tvdb: "5"), _hd),
            Movie(RemoteJellyMesh, "Secondo", 2020, new ContentIds(Tvdb: "5"), _hd));

        Assert.Equal(2, result.Decisions.Count);
        Assert.All(result.Decisions, decision => Assert.Equal(MatchKind.TitleYear, decision.Identity.Match));
    }

    [Theory]
    [InlineData("tt1727587", "TT1727587")]
    [InlineData("tt1727587", " tt1727587 ")]
    public void Ids_IgnoreCaseAndSurroundingSpaces(string first, string second)
    {
        var result = Resolve(
            Movie(Standard, "Sintel", 2010, new ContentIds(Imdb: first), _hd),
            Movie(RemoteJellyMesh, "Sintel", 2010, new ContentIds(Imdb: second), _hd));

        Assert.Single(result.Decisions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void BlankIds_CountAsMissing(string blank)
    {
        var result = Resolve(
            Movie(Standard, "Sintel", 2010, new ContentIds(blank, blank), _hd).Lasting(90),
            Movie(RemoteJellyMesh, "Sintel", 2010, null, _hd).Lasting(90));

        Assert.Equal(MatchKind.TitleYear, Assert.Single(result.Decisions).Identity.Match);
    }

    [Fact]
    public void NoIds_MergeWhenMetadataConfirmTitleAndYear_AndNoIssueIsRaised()
    {
        var result = Resolve(
            Movie(Standard, "L'Éclisse Fantasma (2026)", 2026, null, _hd).Lasting(90),
            Movie(RemoteJellyMesh, "Eclisse Fantasma (2026)", 2026, null, _fullHd).Lasting(90));

        Assert.Equal(MatchKind.TitleYear, Assert.Single(result.Decisions).Identity.Match);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void NoIds_OneYearMissing_StaySeparate()
    {
        var result = Resolve(
            Movie(Standard, "Titolo", 2020, null, _hd),
            Movie(RemoteJellyMesh, "Titolo", null, null, _hd));

        Assert.Equal(2, result.Decisions.Count);
    }

    [Fact]
    public void NoIds_BothYearsMissing_StandAlone()
    {
        var result = Resolve(
            Movie(Standard, "Titolo", null, null, _hd),
            Movie(RemoteJellyMesh, "Titolo", null, null, _hd));

        Assert.Equal(2, result.Decisions.Count);
        Assert.All(result.Decisions, decision => Assert.Equal(MatchKind.ItemOnly, decision.Identity.Match));
    }

    [Fact]
    public void NoIds_NoYear_LocalDoesNotHideARemote()
    {
        var result = DedupEngine.Resolve(
            [Movie(Local, "Titolo", null, null)],
            [Movie(Standard, "Titolo", null, null, _hd)]);

        Assert.False(Assert.Single(result.Decisions).IsLocal);
    }

    [Fact]
    public void NoIds_SeriesWithoutYear_EpisodesStandAlone()
    {
        var result = Resolve(
            Episode(Standard, "Serie", null, null, 1, 1, _hd),
            Episode(RemoteJellyMesh, "Serie", null, null, 1, 1, _hd));

        Assert.Equal(2, result.Decisions.Count);
    }

    [Fact]
    public void Linking_DoesNotDependOnOrder()
    {
        CatalogItem[] items =
        [
            Movie(Standard, "Sintel", 2010, new ContentIds("45745"), _hd),
            Movie(RemoteJellyMesh, "Sintel", 2010, new ContentIds("45745", "tt1"), _hd),
            Movie("server-c", "Sintel", 2010, new ContentIds("45745", "tt2"), _hd),
            Movie("server-d", "Sintel", 2010, new ContentIds(Imdb: "tt2"), _fullHd),
            Movie("server-e", "Sintel", 2010, null, _hd),
        ];

        var expected = Fingerprint(DedupEngine.Resolve([], items));
        foreach (var order in Permutations(items))
        {
            Assert.Equal(expected, Fingerprint(DedupEngine.Resolve([], order)));
        }
    }

    private static DedupResult Resolve(params CatalogItem[] remote) => DedupEngine.Resolve([], remote);
}
