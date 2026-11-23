using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// Knows a home video again by title, year, folder and a duration within 2 s (row 535). Pure and stateless.
/// </summary>
public static class HomeVideoIdentity
{
    private static readonly long _durationTolerance = TimeSpan.FromSeconds(2).Ticks;

    /// <summary>
    /// Pairs each saved home video with every current copy of it.
    /// </summary>
    /// <param name="saved">The saved home videos.</param>
    /// <param name="current">The current home videos.</param>
    /// <param name="maxCopies">The most copies a saved home video is paired with; more leave it unpaired.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pairs, and how many saved home videos have more copies than that.</returns>
    public static SavedPairs<HomeVideoItem> Pair(IReadOnlyCollection<HomeVideoItem> saved, IReadOnlyCollection<HomeVideoItem> current, int maxCopies, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(current);
        var byName = current
            .Where(video => NameKey(video) is not null)
            .DistinctBy(video => (video.ServerId, video.ItemId, video.RunTimeTicks))
            .GroupBy(video => NameKey(video)!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(video => video.RunTimeTicks).ToList(), StringComparer.Ordinal);
        var pairs = new List<(HomeVideoItem Saved, HomeVideoItem Current)>();
        var left = 0;
        foreach (var video in saved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NameKey(video) is not { } key || !byName.TryGetValue(key, out var alike))
            {
                continue;
            }

            var copies = Near(alike, video.RunTimeTicks!.Value, cancellationToken).DistinctBy(copy => (copy.ServerId, copy.ItemId)).Take(maxCopies + 1).ToList();
            if (copies.Count > maxCopies)
            {
                left++;
                continue;
            }

            pairs.AddRange(copies.Select(copy => (video, copy)));
        }

        return new(pairs, left);
    }

    // The videos within 2 s of a duration, of a list sorted by duration.
    private static IEnumerable<HomeVideoItem> Near(List<HomeVideoItem> sorted, long ticks, CancellationToken cancellationToken)
    {
        var (low, high) = (0, sorted.Count);
        while (low < high)
        {
            var middle = (low + high) / 2;
            (low, high) = ticks - sorted[middle].RunTimeTicks!.Value > _durationTolerance ? (middle + 1, high) : (low, middle);
        }

        for (; low < sorted.Count && sorted[low].RunTimeTicks!.Value - ticks <= _durationTolerance; low++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return sorted[low];
        }
    }

    // The normalized title, year and folder, or null without a title or a real duration. A missing year or folder equals a missing one.
    private static string? NameKey(HomeVideoItem video)
    {
        var title = TitleNormalizer.Normalize(video.Title, video.Year);
        return title.Length == 0 || video.RunTimeTicks is not > 0
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"{title}\n{video.Year}\n{TitleNormalizer.Normalize(video.Folder, null)}");
    }
}
