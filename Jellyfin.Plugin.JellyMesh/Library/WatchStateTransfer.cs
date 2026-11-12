using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Carries one user's watch state of an old item onto the new versions of its title (Q41, Q47).
/// </summary>
public static class WatchStateTransfer
{
    /// <summary>
    /// Puts the resume position on the visible version of the last server that served the old item, else of its best
    /// server, else on the primary; favorite, rating, play count and date on the primary if visible, else on the best
    /// visible version; played on every version, by the rule of decision 2 (decisions 149, 153, 154).
    /// </summary>
    /// <param name="from">The user's data on the old item.</param>
    /// <param name="lastServer">The server that last served the old item to the user, null if none is known.</param>
    /// <param name="servers">The servers of the old item, best first (§4.3).</param>
    /// <param name="versions">The new versions of the title, exactly one of them the primary.</param>
    /// <param name="mode">Whether favorite and rating are carried too, only the first time (decision 155).</param>
    /// <returns>The rows that changed, none when the user sees no version.</returns>
    public static IReadOnlyList<UserItemData> Carry(
        UserItemData from,
        Guid? lastServer,
        IReadOnlyList<Guid> servers,
        IReadOnlyList<WatchStateVersion> versions,
        WatchStateCarryMode mode = WatchStateCarryMode.First)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(versions);

        if (versions.Count(version => version.IsPrimary) != 1)
        {
            throw new ArgumentException("A title has exactly one primary version.", nameof(versions));
        }

        var visible = versions.Where(version => version.IsVisible).ToList();
        if (visible.Count == 0)
        {
            return [];
        }

        var primary = visible.Where(version => version.IsPrimary).Concat(ByServer(servers, visible)).Concat(visible).First().Data;
        var target = ByServer(lastServer is { } last ? servers.Prepend(last) : servers, visible)
            .Concat(visible.Where(version => version.Data == primary))
            .First().Data;

        // All decided before any change: the target can be the primary.
        var toTarget = WatchStateMerge.IsNewer(from, target);
        var toPrimary = WatchStateMerge.IsNewer(from, primary);
        var played = HasWatchState(from)
            ? PlayedAlignment.Decide(versions.Select(version => version.Data).Prepend(from))
            : null;

        var changed = new HashSet<UserItemData>(ReferenceEqualityComparer.Instance);
        if (toTarget)
        {
            target.PlaybackPositionTicks = from.PlaybackPositionTicks;
            target.LastPlayedDate = from.LastPlayedDate;
            changed.Add(target);
        }

        if (toPrimary)
        {
            primary.PlayCount = from.PlayCount;
            primary.LastPlayedDate = from.LastPlayedDate;
            changed.Add(primary);
        }

        if (mode == WatchStateCarryMode.First && FillFavoriteAndRating(from, primary))
        {
            changed.Add(primary);
        }

        // Jellyfin spreads played only on a playback or mark event, not on an import: the flag goes on every version (D4).
        if (played is { } flag)
        {
            foreach (var version in versions.Where(version => version.Data.Played != flag))
            {
                version.Data.Played = flag;
                changed.Add(version.Data);
            }
        }

        return changed.ToList();
    }

    /// <summary>
    /// Puts the favorite and the rating of an old series on each new series stub the user sees (decision 150).
    /// </summary>
    /// <param name="from">The user's data on the old series.</param>
    /// <param name="series">The user's data on each visible new series, changed in place.</param>
    /// <returns>The rows that changed.</returns>
    public static IReadOnlyList<UserItemData> CarrySeries(UserItemData from, IReadOnlyList<UserItemData> series)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(series);

        return series.Where(data => FillFavoriteAndRating(from, data)).ToList();
    }

    /// <summary>
    /// Copies a row, so that a carry changes only what it saves.
    /// </summary>
    /// <param name="data">The row.</param>
    /// <returns>The copy.</returns>
    public static UserItemData Copy(UserItemData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        // Likes follows Rating, so it is not copied on its own.
        return new UserItemData
        {
            Key = data.Key,
            Rating = data.Rating,
            PlaybackPositionTicks = data.PlaybackPositionTicks,
            PlayCount = data.PlayCount,
            IsFavorite = data.IsFavorite,
            LastPlayedDate = data.LastPlayedDate,
            Played = data.Played,
            AudioStreamIndex = data.AudioStreamIndex,
            SubtitleStreamIndex = data.SubtitleStreamIndex
        };
    }

    // A favorite stays one, and a rating only fills a gap.
    private static bool FillFavoriteAndRating(UserItemData from, UserItemData to)
    {
        var changed = false;
        if (from.IsFavorite && !to.IsFavorite)
        {
            to.IsFavorite = true;
            changed = true;
        }

        if (from.Rating is not null && to.Rating is null)
        {
            to.Rating = from.Rating;
            changed = true;
        }

        return changed;
    }

    // A row with watch state is newer than an empty one.
    private static bool HasWatchState(UserItemData data) => WatchStateMerge.IsNewer(data, new UserItemData { Key = data.Key });

    private static IEnumerable<WatchStateVersion> ByServer(IEnumerable<Guid> servers, IReadOnlyList<WatchStateVersion> versions) =>
        servers.SelectMany(server => versions.Where(version => version.ServerId is { } id && id == server).Take(1));
}
