using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
    /// <returns>The pairs.</returns>
    public static IReadOnlyList<(HomeVideoItem Saved, HomeVideoItem Current)> Pair(IReadOnlyCollection<HomeVideoItem> saved, IReadOnlyCollection<HomeVideoItem> current)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(current);
        var byName = current.Where(video => NameKey(video) is not null).ToLookup(video => NameKey(video)!, StringComparer.Ordinal);
        return saved
            .Where(video => NameKey(video) is not null)
            .SelectMany(video => byName[NameKey(video)!]
                .Where(copy => Math.Abs(copy.RunTimeTicks!.Value - video.RunTimeTicks!.Value) <= _durationTolerance)
                .DistinctBy(copy => (copy.ServerId, copy.ItemId))
                .Select(copy => (video, copy)))
            .ToList();
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
