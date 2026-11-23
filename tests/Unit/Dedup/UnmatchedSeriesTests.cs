using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;
using static Jellyfin.Plugin.JellyMesh.Tests.Dedup.Fixtures;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

/// <summary>
/// A remote series without IDs and without a title or a year has no work. Its episodes stay together as a series of their
/// server, and are merged with no other copy.
/// </summary>
public class UnmatchedSeriesTests
{
    private static readonly SourceQuality _sd = Quality(480, 1000000);
    private static readonly SourceQuality _hd = Quality(720, 2000000);

    private static CatalogItem Unmatched(string server, int? season, int? episode, string id, SourceQuality? quality = null)
        => Episode(server, "Show", null, null, season, episode, quality ?? _hd) with { ItemId = id };

    [Fact]
    public void ItsEpisodes_ShareAWork()
    {
        var result = DedupEngine.Resolve([], [Unmatched(Standard, 1, 1, "a"), Unmatched(Standard, 1, 2, "b"), Unmatched(Standard, 2, 1, "c")]);

        Assert.Equal(3, result.Decisions.Select(decision => decision.Identity.Key).Distinct().Count());
        Assert.Single(result.Decisions.Select(decision => decision.Identity.WorkKey).Distinct());
        Assert.All(result.Decisions, decision => Assert.Equal(MatchKind.ItemOnly, decision.Identity.Match));
        Assert.Empty(result.Issues);
        Assert.Empty(result.MergeCases);
    }

    [Fact]
    public void TwoFilesOfAnEpisode_AreOneTitleWithABackup()
    {
        var decision = Assert.Single(DedupEngine.Resolve([], [Unmatched(Standard, 1, 1, "a", _sd), Unmatched(Standard, 1, 1, "b", _hd)]).Decisions);

        Assert.Equal("b", decision.Winner!.ItemId);
        Assert.Equal(["a"], decision.Backups.Select(backup => backup.ItemId));
    }

    [Fact]
    public void AFileOfSeveralEpisodes_CountsOnceForEachOfThem()
    {
        var result = DedupEngine.Resolve([], [Unmatched(Standard, 1, 1, "a") with { EpisodeNumberEnd = 2 }, Unmatched(Standard, 1, 2, "b", _sd)]);

        Assert.Equal([1, 2], result.Decisions.Select(decision => decision.Identity.EpisodeNumber!.Value));
        Assert.All(result.Decisions, decision => Assert.Equal("a", decision.Winner!.ItemId));
        Assert.Equal(["b"], result.Decisions[1].Backups.Select(backup => backup.ItemId));
    }

    [Fact]
    public void TheSameSeriesOfAnotherServerOrOfThisServer_IsAnotherOne()
    {
        var result = DedupEngine.Resolve(
            [Episode(Local, "Show", null, null, 1, 1)],
            [Unmatched(Standard, 1, 1, "a"), Unmatched(RemoteJellyMesh, 1, 1, "a"), Unmatched(Standard, 1, 1, "b") with { SeriesId = "other" }]);

        Assert.Equal(3, result.Decisions.Select(decision => decision.Identity.WorkKey).Distinct().Count());
        Assert.All(result.Decisions, decision => Assert.False(decision.IsLocal));
    }

    [Theory]
    [InlineData("series", null, 1)]
    [InlineData("series", 1, null)]
    [InlineData(null, 1, 1)]
    public void AnEpisodeWithoutItsNumbersOrSeries_StaysAlone(string? seriesId, int? season, int? episode)
    {
        var result = DedupEngine.Resolve([], [
            Unmatched(Standard, season, episode, "a") with { SeriesId = seriesId },
            Unmatched(Standard, season, episode, "b") with { SeriesId = seriesId }
        ]);

        Assert.Equal(2, result.Decisions.Select(decision => decision.Identity.WorkKey).Distinct().Count());
        Assert.All(result.Decisions, decision => Assert.Equal(decision.Identity.Key, decision.Identity.WorkKey));
    }
}
