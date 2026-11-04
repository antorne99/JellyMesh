using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;
using static Jellyfin.Plugin.JellyMesh.Tests.Dedup.Fixtures;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

public class QualityRankingTests
{
    [Fact]
    public void Resolution_BeatsBitrate()
    {
        var low = Source("a", Quality(720, 5969220));
        var high = Source("b", Quality(1080, 2767876));

        AssertRanking([high, low]);
    }

    [Fact]
    public void Resolution_BeatsHdrAndCodec()
    {
        var hdr = Source("a", Quality(720, 9000000, "av1", hdr: true));
        var high = Source("b", Quality(1080, 1000000, "h264"));

        AssertRanking([high, hdr]);
    }

    [Fact]
    public void Hdr_BeatsBitrateAndCodec()
    {
        var sdr = Source("a", Quality(1080, 5917850, "av1"));
        var hdr = Source("b", Quality(1080, 2931727, "h264", hdr: true));

        AssertRanking([hdr, sdr]);
    }

    [Fact]
    public void HdrFormats_AreOneTier_SoBitrateDecidesBetweenThem()
    {
        var lower = Source("a", Quality(1080, 3000000, hdr: true));
        var higher = Source("b", Quality(1080, 4000000, hdr: true));

        AssertRanking([higher, lower]);
    }

    [Fact]
    public void Bitrate_DecidesOutsideTheTolerance()
    {
        var high = Source("a", Quality(1080, 5938499));
        var low = Source("b", Quality(1080, 2782266, "av1"));

        AssertRanking([high, low]);
    }

    [Fact]
    public void Tolerance_LetsCodecDecide_InTheObserved36PercentCase()
    {
        var h264 = Source("a", Quality(1080, 2749552, "h264"));
        var av1 = Source("b", Quality(1080, 2649413, "av1"));

        AssertRanking([av1, h264]);
    }

    [Fact]
    public void Tolerance_IncludesExactly95Percent()
    {
        var highest = Source("a", Quality(1080, 1000000, "h264"));
        var at95 = Source("b", Quality(1080, 950000, "av1"));

        AssertRanking([at95, highest]);
    }

    [Fact]
    public void Tolerance_ExcludesJustBelow95Percent()
    {
        var highest = Source("a", Quality(1080, 1000000, "h264"));
        var below = Source("b", Quality(1080, 949999, "av1"));

        AssertRanking([highest, below]);
    }

    [Fact]
    public void Tolerance_IsRelativeToTheHighestBitrate_NotPairwise()
    {
        // C is within 5% of B but not of A. A pairwise comparison would make the result depend on the order.
        var a = Source("a", Quality(1080, 1000000, "h264"));
        var b = Source("b", Quality(1080, 960000, "h264"));
        var c = Source("c", Quality(1080, 920000, "av1"));

        Assert.Equal(a, QualityRanking.Rank([a, b, c])[0]);
        foreach (var order in Permutations<RemoteSource>([a, b, c]))
        {
            Assert.Equal(QualityRanking.Rank([a, b, c]), QualityRanking.Rank(order));
        }
    }

    [Fact]
    public void Tolerance_DoesNotOverflowOnLargeBitrates()
    {
        var highest = Source("a", Quality(2160, int.MaxValue, "h264"));
        var close = Source("b", Quality(2160, int.MaxValue - 1000, "av1"));

        AssertRanking([close, highest]);
    }

    [Fact]
    public void WithinTolerance_SameCodec_HigherBitrateWins()
    {
        var lower = Source("a", Quality(1080, 980000));
        var higher = Source("z", Quality(1080, 1000000));

        AssertRanking([higher, lower]);
    }

    [Theory]
    [InlineData("av1", "hevc")]
    [InlineData("hevc", "vp9")]
    [InlineData("vp9", "h264")]
    [InlineData("h264", "mpeg2video")]
    [InlineData("h264", null)]
    [InlineData("AV1", "HEVC")]
    [InlineData("h265", "avc")]
    public void Codec_MoreEfficientWins(string? better, string? worse)
    {
        // The worse codec has the higher bitrate and the earlier server, so only the codec can decide.
        var loser = new RemoteSource("a", "item", "item", new SourceQuality(1080, false, 1000000, worse));
        var winner = new RemoteSource("b", "item", "item", new SourceQuality(1080, false, 990000, better));

        AssertRanking([winner, loser]);
    }

    [Fact]
    public void MissingHeight_CountsAsLowest()
    {
        var unknown = new RemoteSource("a", "item", "item", new SourceQuality(null, false, 9000000, "h264"));
        var known = Source("b", Quality(480, 1000000));

        AssertRanking([known, unknown]);
    }

    [Fact]
    public void MissingBitrate_CountsAsLowest()
    {
        var unknown = new RemoteSource("a", "item", "item", new SourceQuality(1080, false, null, "av1"));
        var known = Source("b", Quality(1080, 1000000));

        AssertRanking([known, unknown]);
    }

    [Fact]
    public void AllBitratesMissing_CodecDecides()
    {
        var h264 = new RemoteSource("a", "item", "item", new SourceQuality(1080, false, null, "h264"));
        var hevc = new RemoteSource("b", "item", "item", new SourceQuality(1080, false, null, "hevc"));

        AssertRanking([hevc, h264]);
    }

    [Fact]
    public void ExactTies_AreOrderedByIdsSoTheResultIsStable()
    {
        var quality = Quality(1080, 1000000);
        var first = new RemoteSource("a", "item-1", "v1", quality);
        var second = new RemoteSource("a", "item-1", "v2", quality);
        var third = new RemoteSource("a", "item-2", "v1", quality);
        var fourth = new RemoteSource("b", "item-1", "v1", quality);

        AssertRanking([first, second, third, fourth]);
    }

    [Fact]
    public void Rank_DropsRepeatedSources()
    {
        var source = Source("a", Quality(1080, 1000000));

        Assert.Equal([source], QualityRanking.Rank([source, source with { }]));
    }

    [Fact]
    public void Rank_HandlesAnEmptyGroup()
    {
        Assert.Empty(QualityRanking.Rank([]));
    }

    [Fact]
    public void Backups_FollowTheSameQuality()
    {
        var best = Source("a", Quality(2160, 9000000, "hevc", hdr: true));
        var second = Source("b", Quality(1080, 5000000, "hevc", hdr: true));
        var third = Source("c", Quality(1080, 7000000, "h264"));
        var fourth = Source("d", Quality(720, 9000000, "av1"));

        AssertRanking([best, second, third, fourth]);
    }

    [Fact]
    public void Backups_ApplyTheToleranceAmongTheSourcesLeft()
    {
        // Once A is gone, B and C are within 5% of each other, so the codec decides between them.
        var a = Source("a", Quality(1080, 1000000, "av1"));
        var b = Source("b", Quality(1080, 960000, "h264"));
        var c = Source("c", Quality(1080, 920000, "hevc"));

        AssertRanking([a, c, b]);
    }

    [Fact]
    public void RandomGroups_DoNotDependOnOrder_AndTheFirstBackupWinsWithoutTheWinner()
    {
        var random = new Random(4021);
        for (var round = 0; round < 500; round++)
        {
            var sources = RandomSources(random);
            var ranked = QualityRanking.Rank(sources);

            Assert.Equal(ranked, QualityRanking.Rank(Shuffled(sources, random)));
            Assert.Equal(ranked.Skip(1), QualityRanking.Rank(sources.Where(source => source != ranked[0])));
        }
    }

    private static void AssertRanking(IReadOnlyList<RemoteSource> expected)
    {
        foreach (var order in Permutations(expected))
        {
            Assert.Equal(expected, QualityRanking.Rank(order));
        }
    }

    private static List<RemoteSource> RandomSources(Random random)
    {
        int[] heights = [720, 1080, 2160];
        int[] bitRates = [900000, 930000, 950000, 960000, 1000000, 1040000];
        string[] codecs = ["h264", "hevc", "av1", "vp9"];

        return Enumerable.Range(0, random.Next(1, 8))
            .Select(index => new RemoteSource(
                "server-" + index,
                "item",
                "item",
                new SourceQuality(
                    heights[random.Next(heights.Length)],
                    random.Next(2) == 0,
                    bitRates[random.Next(bitRates.Length)],
                    codecs[random.Next(codecs.Length)])))
            .ToList();
    }
}
