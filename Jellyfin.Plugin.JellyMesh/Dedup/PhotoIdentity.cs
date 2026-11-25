using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// Knows a photo again by title, year and folder (row 565). Pure and stateless.
/// </summary>
public static class PhotoIdentity
{
    /// <summary>
    /// Pairs each saved photo with every current copy of it.
    /// </summary>
    /// <param name="saved">The saved photos.</param>
    /// <param name="current">The current photos.</param>
    /// <param name="maxCopies">The most copies a saved photo is paired with; more leave it unpaired.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pairs, and how many saved photos have more copies than that.</returns>
    public static SavedPairs<PhotoItem> Pair(IReadOnlyCollection<PhotoItem> saved, IReadOnlyCollection<PhotoItem> current, int maxCopies, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(current);
        var byName = current
            .Where(photo => NameKey(photo) is not null)
            .GroupBy(photo => NameKey(photo)!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.DistinctBy(photo => (photo.ServerId, photo.ItemId)).ToList(), StringComparer.Ordinal);
        var pairs = new List<(PhotoItem Saved, PhotoItem Current)>();
        var left = 0;
        foreach (var photo in saved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NameKey(photo) is not { } key || !byName.TryGetValue(key, out var copies))
            {
                continue;
            }

            if (copies.Count > maxCopies)
            {
                left++;
                continue;
            }

            pairs.AddRange(copies.Select(copy => (photo, copy)));
        }

        return new(pairs, left);
    }

    // The normalized title, year and folder, or null without a title. A missing year or folder equals a missing one.
    private static string? NameKey(PhotoItem photo)
    {
        var title = TitleNormalizer.Normalize(photo.Title, photo.Year);
        return title.Length == 0
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"{title}\n{photo.Year}\n{TitleNormalizer.Normalize(photo.Folder, null)}");
    }
}
