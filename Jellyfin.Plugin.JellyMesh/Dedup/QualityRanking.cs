using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// The quality comparison of plan §4.3.
/// </summary>
/// <remarks>
/// This is a selection over a group and deliberately not an <see cref="IComparer{T}"/>: the bitrate tolerance is relative
/// to the highest bitrate in the group, so a pairwise comparison would not be transitive and its result would depend on
/// the order of the sources.
/// </remarks>
public static class QualityRanking
{
    private const long BitRateTolerancePercent = 95;

    /// <summary>
    /// Ranks sources best first. Each position holds the winner among the sources not yet placed, so the first backup is
    /// exactly what would have won without the winner. The result does not depend on the input order: sources that tie
    /// on every criterion are ordered by server, item and media source ID.
    /// </summary>
    /// <param name="sources">The sources.</param>
    /// <returns>The distinct sources, best first.</returns>
    public static IReadOnlyList<RemoteSource> Rank(IEnumerable<RemoteSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var remaining = sources.Distinct().ToList();
        var ranked = new List<RemoteSource>(remaining.Count);
        while (remaining.Count > 0)
        {
            var best = SelectBest(remaining);
            ranked.Add(best);
            remaining.Remove(best);
        }

        return ranked;
    }

    private static RemoteSource SelectBest(List<RemoteSource> sources)
    {
        var maxHeight = sources.Max(Height);
        var tallest = sources.Where(source => Height(source) == maxHeight).ToList();

        var anyHdr = tallest.Any(source => source.Quality.IsHdr);
        var bestRange = tallest.Where(source => source.Quality.IsHdr == anyHdr).ToList();

        var maxBitRate = bestRange.Max(BitRate);
        return bestRange
            .Where(source => BitRate(source) * 100 >= maxBitRate * BitRateTolerancePercent)
            .OrderByDescending(source => CodecRank(source.Quality.Codec))
            .ThenByDescending(BitRate)
            .ThenBy(source => source.ServerId, StringComparer.Ordinal)
            .ThenBy(source => source.ItemId, StringComparer.Ordinal)
            .ThenBy(source => source.MediaSourceId, StringComparer.Ordinal)
            .First();
    }

    private static int Height(RemoteSource source) => source.Quality.Height ?? 0;

    private static long BitRate(RemoteSource source) => source.Quality.BitRate ?? 0;

    // The plan gives AV1 > HEVC > H.264 as examples of "most efficient first". VP9 sits between HEVC and H.264,
    // and anything else, unknown codecs included, comes last.
    private static int CodecRank(string? codec) => codec?.ToUpperInvariant() switch
    {
        "AV1" => 4,
        "HEVC" or "H265" => 3,
        "VP9" => 2,
        "H264" or "AVC" => 1,
        _ => 0,
    };
}
