using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

public class AudioQualityRankingTests
{
    private static RemoteSource Source(string server, string? codec, int? bitDepth = null, int? sampleRate = 44100, int? bitRate = 320000) =>
        new(server, "item-" + server, "source-" + server, SourceQuality.Audio(codec, bitDepth, sampleRate, bitRate));

    [Fact]
    public void LosslessBeatsLossyWhateverItsRateAndBitrate()
    {
        var mp3 = Source("a", "mp3", sampleRate: 96000, bitRate: 2_000_000);
        var flac = Source("b", "flac", 16, 44100, 900_000);

        Assert.Equal([flac, mp3], QualityRanking.RankAudio([mp3, flac]));
    }

    [Theory]
    [InlineData("alac")]
    [InlineData("ape")]
    [InlineData("FLAC")]
    [InlineData("mlp")]
    [InlineData("truehd")]
    [InlineData("wavpack")]
    [InlineData("pcm_s16le")]
    [InlineData("pcm_s24be")]
    [InlineData("pcm_f32le")]
    public void EveryLosslessCodecOfTheSpecBeatsAnOpusOfHigherBitrate(string codec)
    {
        var opus = Source("a", "opus", sampleRate: 48000, bitRate: 5_000_000);
        var lossless = Source("b", codec, 16, 44100, 100_000);

        Assert.Equal([lossless, opus], QualityRanking.RankAudio([opus, lossless]));
    }

    [Fact]
    public void BitDepthDecidesBetweenLosslessCopiesBeforeTheSampleRate()
    {
        var deep = Source("a", "flac", 24, 44100, 1_000_000);
        var fast = Source("b", "flac", 16, 96000, 2_000_000);

        Assert.Equal([deep, fast], QualityRanking.RankAudio([fast, deep]));
    }

    [Fact]
    public void SampleRateDecidesBeforeTheBitrate()
    {
        var fast = Source("a", "aac", sampleRate: 48000, bitRate: 128_000);
        var dense = Source("b", "aac", sampleRate: 44100, bitRate: 320_000);

        Assert.Equal([fast, dense], QualityRanking.RankAudio([dense, fast]));
    }

    [Fact]
    public void BitrateDecidesOutsideTheFivePercentTolerance()
    {
        var high = Source("a", "mp3", bitRate: 320_000);
        var low = Source("b", "opus", bitRate: 256_000);

        Assert.Equal([high, low], QualityRanking.RankAudio([low, high]));
    }

    [Fact]
    public void WithinTheFivePercentTheCodecDecides()
    {
        var mp3 = Source("a", "mp3", bitRate: 320_000);
        var opus = Source("b", "opus", bitRate: 304_000);

        Assert.Equal([opus, mp3], QualityRanking.RankAudio([mp3, opus]));
    }

    [Fact]
    public void LossyCodecsGoMostEfficientFirst()
    {
        var other = Source("a", "ac3");
        var mp3 = Source("b", "mp3");
        var vorbis = Source("c", "vorbis");
        var aac = Source("d", "aac");
        var opus = Source("e", "opus");

        Assert.Equal([opus, aac, vorbis, mp3, other], QualityRanking.RankAudio([other, mp3, vorbis, aac, opus]));
    }

    [Fact]
    public void LosslessCodecsGoMostEfficientFirst()
    {
        var pcm = Source("a", "pcm_s16le", 16);
        var mlp = Source("b", "mlp", 16);
        var truehd = Source("c", "truehd", 16);
        var ape = Source("d", "ape", 16);
        var wavpack = Source("e", "wavpack", 16);
        var alac = Source("f", "alac", 16);
        var flac = Source("g", "flac", 16);

        Assert.Equal([flac, alac, wavpack, ape, truehd, mlp, pcm], QualityRanking.RankAudio([pcm, mlp, truehd, ape, wavpack, alac, flac]));
    }

    [Fact]
    public void TheSameCodecWithinTheToleranceTakesTheHigherBitrate()
    {
        var lower = Source("a", "flac", 16, 44100, 990_000);
        var higher = Source("b", "flac", 16, 44100, 1_000_000);

        Assert.Equal([higher, lower], QualityRanking.RankAudio([lower, higher]));
    }

    [Fact]
    public void MissingValuesCountAsLowestAndAnUnknownCodecAsLossy()
    {
        var unknown = Source("a", null, null, null, null);
        var mp3 = Source("b", "mp3", bitRate: 128_000);

        Assert.Equal([mp3, unknown], QualityRanking.RankAudio([unknown, mp3]));
    }

    [Fact]
    public void TheOrderDoesNotDependOnTheInput()
    {
        RemoteSource[] sources = [Source("c", "flac", 16), Source("a", "flac", 16), Source("b", "flac", 16), Source("d", "mp3")];

        foreach (var order in Fixtures.Permutations<RemoteSource>(sources))
        {
            Assert.Equal(QualityRanking.RankAudio(sources), QualityRanking.RankAudio(order));
        }

        Assert.Equal(["a", "b", "c", "d"], QualityRanking.RankAudio(sources).Select(source => source.ServerId));
    }
}
