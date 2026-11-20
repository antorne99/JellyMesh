using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;
using static Jellyfin.Plugin.JellyMesh.Tests.Dedup.Fixtures;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

/// <summary>
/// Row 454 and D64: a music video is one title by IMDb or TMDb ID, else by the same artists and normalized title, with the
/// year when both copies have one. Its copies on several servers are versions, ranked by §4.3, like a film.
/// </summary>
public class MusicVideoIdentityTests
{
    private const string Third = "terzo";

    private static readonly SourceQuality _sd = Quality(480, 1000000);
    private static readonly SourceQuality _hd = Quality(720, 2000000);
    private static readonly SourceQuality _fullHd = Quality(1080, 4000000);

    private static readonly string[] _artists = ["Daft Punk"];

    private static DedupResult Resolve(params CatalogItem[] remote) => DedupEngine.Resolve([], remote);

    [Fact]
    public void CopiesWithOneImdbId_AreOneTitle_TheBestQualityWins_AndTheOtherIsABackup()
    {
        var result = Resolve(
            MusicVideo(Standard, "Around the World", 1997, new ContentIds(null, "tt0001"), _artists, _hd),
            MusicVideo(RemoteJellyMesh, "Around The World (Live)", 1997, new ContentIds(null, "tt0001"), ["Altro"], _fullHd));

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(ContentKind.MusicVideo, decision.Identity.Kind);
        Assert.Equal(MatchKind.ProviderId, decision.Identity.Match);
        Assert.Equal(1080, decision.Winner!.Quality.Height);
        Assert.Equal([720], decision.Backups.Select(backup => backup.Quality.Height!.Value));
    }

    [Fact]
    public void CopiesWithoutIds_WithTheSameArtistsInAnyOrderAndTheSameTitle_AreOneTitle()
    {
        var result = Resolve(
            MusicVideo(Standard, "Get Lucky", 2013, null, ["Daft Punk", "Pharrell Williams"], _hd),
            MusicVideo(RemoteJellyMesh, "GET LUCKY!", 2013, null, ["pharrell williams", "Daft Punk"], _sd));

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(MatchKind.TitleYear, decision.Identity.Match);
        Assert.Equal(720, decision.Winner!.Quality.Height);
        Assert.Single(decision.Backups);
    }

    [Fact]
    public void OtherArtists_MakeAnotherTitle()
    {
        var result = Resolve(
            MusicVideo(Standard, "Get Lucky", 2013, null, ["Daft Punk", "Pharrell Williams"], _hd),
            MusicVideo(RemoteJellyMesh, "Get Lucky", 2013, null, ["Daft Punk"], _sd));

        Assert.Equal(2, result.Decisions.Count);
    }

    [Fact]
    public void CopiesWithNoArtist_DoNotMatchByTitle()
    {
        var result = Resolve(
            MusicVideo(Standard, "Get Lucky", 2013, null, [], _hd),
            MusicVideo(RemoteJellyMesh, "Get Lucky", 2013, null, [" "], _sd));

        Assert.Equal(2, result.Decisions.Count);
        Assert.All(result.Decisions, decision => Assert.Equal(MatchKind.ItemOnly, decision.Identity.Match));
        Assert.Empty(result.MergeCases);
    }

    [Fact]
    public void ACopyWithoutAYear_JoinsTheCopiesOfTheOnlyYear()
    {
        var result = Resolve(
            MusicVideo(Standard, "One More Time", 2000, null, _artists, _hd),
            MusicVideo(RemoteJellyMesh, "One More Time", null, null, _artists, _sd),
            MusicVideo(Third, "One More Time", 2000, null, _artists, _fullHd) with { ItemId = "altro" });

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(3, decision.RemoteItems.Count);
        Assert.Empty(result.MergeCases);
    }

    [Fact]
    public void CopiesWithoutAnyYear_AreOneTitle()
    {
        var result = Resolve(
            MusicVideo(Standard, "One More Time", null, null, _artists, _hd),
            MusicVideo(RemoteJellyMesh, "One More Time", null, null, _artists, _sd));

        Assert.Single(result.Decisions);
    }

    [Fact]
    public void WithTwoYears_EachYearIsATitle_AndACopyWithoutAYearStaysAlone_AsAPossibleDuplicate()
    {
        var result = Resolve(
            MusicVideo(Standard, "One More Time", 2000, null, _artists, _hd),
            MusicVideo(RemoteJellyMesh, "One More Time", 2000, null, _artists, _sd) with { ItemId = "stesso-anno" },
            MusicVideo(Third, "One More Time", 2005, null, _artists, _fullHd) with { ItemId = "altro-anno" },
            MusicVideo(Third, "One More Time", null, null, _artists, _sd) with { ItemId = "senza-anno" });

        Assert.Equal(3, result.Decisions.Count);
        Assert.Equal([2, 1, 1], result.Decisions.Select(decision => decision.RemoteItems.Count).OrderDescending());
        var group = Assert.Single(result.MergeCases);
        Assert.Equal(DedupIssueKind.PossibleDuplicate, group.Kind);
        Assert.Equal(ContentKind.MusicVideo, group.ContentKind);
        Assert.Equal(3, group.Works.Count);
    }

    [Fact]
    public void TwoYearsWithNoCopyWithoutAYear_AreTwoTitles_AndNoCase()
    {
        var result = Resolve(
            MusicVideo(Standard, "One More Time", 2000, null, _artists, _hd),
            MusicVideo(RemoteJellyMesh, "One More Time", 2005, null, _artists, _sd));

        Assert.Equal(2, result.Decisions.Count);
        Assert.Empty(result.MergeCases);
    }

    [Fact]
    public void ACopyWithoutIds_JoinsTheOnlyCopyWithIds_ByArtistsAndTitle()
    {
        var result = Resolve(
            MusicVideo(Standard, "Harder Better", 2001, new ContentIds("55", "tt0002"), _artists, _hd),
            MusicVideo(RemoteJellyMesh, "Harder Better", null, null, _artists, _fullHd));

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(MatchKind.ProviderId, decision.Identity.Match);
        Assert.Equal(1080, decision.Winner!.Quality.Height);
    }

    [Fact]
    public void ACopyWithoutIds_BetweenTwoSetsOfIds_StaysAlone_AsACase()
    {
        var result = Resolve(
            MusicVideo(Standard, "Harder Better", 2001, new ContentIds(null, "tt0002"), _artists, _hd),
            MusicVideo(RemoteJellyMesh, "Harder Better", 2001, new ContentIds(null, "tt0003"), _artists, _sd),
            MusicVideo(Third, "Harder Better", 2001, null, _artists, _fullHd));

        Assert.Equal(3, result.Decisions.Count);
        var group = Assert.Single(result.MergeCases);
        Assert.Equal(3, group.Works.Count);
    }

    [Fact]
    public void AFilmAndAMusicVideo_WithOneId_StayApart()
    {
        var result = Resolve(
            Movie(Standard, "Interstella 5555", 2003, new ContentIds("77", "tt0004"), _hd),
            MusicVideo(RemoteJellyMesh, "Interstella 5555", 2003, new ContentIds("77", "tt0004"), _artists, _sd));

        Assert.Equal([ContentKind.Movie, ContentKind.MusicVideo], result.Decisions.Select(decision => decision.Identity.Kind).Order());
    }

    [Fact]
    public void TheTvdbIdOfAMusicVideo_IsNotRead()
    {
        var result = Resolve(
            MusicVideo(Standard, "Primo", 2003, new ContentIds(null, null, "9"), _artists, _hd),
            MusicVideo(RemoteJellyMesh, "Secondo", 2003, new ContentIds(null, null, "9"), _artists, _sd));

        Assert.Equal(2, result.Decisions.Count);
    }

    [Fact]
    public void TheResult_DoesNotDependOnTheOrderOfTheItems()
    {
        CatalogItem[] items =
        [
            MusicVideo(Standard, "One More Time", 2000, null, _artists, _hd),
            MusicVideo(RemoteJellyMesh, "One More Time", 2005, null, _artists, _sd) with { ItemId = "b" },
            MusicVideo(Third, "One More Time", null, null, _artists, _fullHd) with { ItemId = "c" },
            MusicVideo(Third, "One More Time", 2000, new ContentIds(null, "tt0005"), _artists, _sd) with { ItemId = "d" }
        ];

        var expected = Fingerprint(DedupEngine.Resolve([], items));
        foreach (var order in Permutations(items))
        {
            Assert.Equal(expected, Fingerprint(DedupEngine.Resolve([], order)));
        }
    }
}
