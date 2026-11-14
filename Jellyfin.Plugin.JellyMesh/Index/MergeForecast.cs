using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// What would play if titles were merged, by the rules a merged title follows (decision 20, plan §4.3).
/// </summary>
/// <param name="Copy">The copy that would be chosen as the catalog stands, null if there is nothing to choose from.</param>
/// <param name="Version">Its best version, null for a local copy, whose quality is not read.</param>
/// <param name="IsCertain">Whether the choice holds whatever the values nobody knows turn out to be.</param>
public sealed record MergeForecast(MergeCopyView? Copy, MediaVersion? Version, bool IsCertain)
{
    /// <summary>
    /// Works out what would play. A local copy wins, otherwise <see cref="QualityRanking"/> decides. A series is decided
    /// per episode, so it has no single answer and gets none.
    /// </summary>
    /// <param name="titles">The titles that would be merged.</param>
    /// <returns>The forecast.</returns>
    public static MergeForecast Of(IEnumerable<MergeTitleView> titles)
    {
        var copies = titles.SelectMany(title => title.Copies).ToList();
        if (copies.Exists(copy => copy.Copy.Kind != ContentKind.Movie))
        {
            return new MergeForecast(null, null, false);
        }

        if (copies.Find(copy => copy.Copy.IsLocal) is { } local)
        {
            return new MergeForecast(local, null, true);
        }

        var sources = copies
            .SelectMany(copy => copy.Versions.Select(version => new RemoteSource(copy.Copy.ServerId, copy.Copy.ItemId, version.MediaSourceId, version.Quality)))
            .ToList();
        if (sources.Count == 0)
        {
            return new MergeForecast(null, null, false);
        }

        var best = QualityRanking.Rank(sources)[0];
        return new MergeForecast(
            copies.Find(copy => string.Equals(copy.Copy.ServerId, best.ServerId, StringComparison.Ordinal) && string.Equals(copy.Copy.ItemId, best.ItemId, StringComparison.Ordinal)),
            new MediaVersion(best.MediaSourceId, best.Quality),
            !QualityRanking.DependsOnUnknown(sources));
    }
}
