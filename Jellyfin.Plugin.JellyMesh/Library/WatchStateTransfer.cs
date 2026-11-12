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
    /// Puts the resume position on the version of the last server that served the old item, else of its best server, else
    /// on the primary; favorite, rating and play count on the primary; played on every version, by the rule of decision 2.
    /// </summary>
    /// <param name="from">The user's data on the old item.</param>
    /// <param name="lastServer">The server that last served the old item to the user, null if none is known.</param>
    /// <param name="servers">The servers of the old item, best first (§4.3).</param>
    /// <param name="versions">The new versions of the title, exactly one of them the primary.</param>
    /// <returns>The rows that changed.</returns>
    public static IReadOnlyList<UserItemData> Carry(UserItemData from, Guid? lastServer, IReadOnlyList<Guid> servers, IReadOnlyList<WatchStateVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(versions);

        if (versions.Count(version => version.IsPrimary) != 1)
        {
            throw new ArgumentException("A title has exactly one primary version.", nameof(versions));
        }

        var primary = versions.Single(version => version.IsPrimary).Data;
        var target = PositionTarget(lastServer, servers, versions).Data;

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

        if (from.IsFavorite && !primary.IsFavorite)
        {
            primary.IsFavorite = true;
            changed.Add(primary);
        }

        if (from.Rating is not null && primary.Rating is null)
        {
            primary.Rating = from.Rating;
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

    // A row with watch state is newer than an empty one.
    private static bool HasWatchState(UserItemData data) => WatchStateMerge.IsNewer(data, new UserItemData { Key = data.Key });

    private static WatchStateVersion PositionTarget(Guid? lastServer, IReadOnlyList<Guid> servers, IReadOnlyList<WatchStateVersion> versions)
    {
        foreach (var server in lastServer is { } last ? servers.Prepend(last) : servers)
        {
            if (versions.FirstOrDefault(version => version.ServerId is { } id && id == server) is { } found)
            {
                return found;
            }
        }

        return versions.Single(version => version.IsPrimary);
    }
}
