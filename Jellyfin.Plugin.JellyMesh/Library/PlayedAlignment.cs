using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Decides the played flag of the versions of a title for one user (D4): the version played last decides.
/// </summary>
public static class PlayedAlignment
{
    /// <summary>
    /// Decides the played flag. Only versions with some watch state count; without a date, a played one wins.
    /// </summary>
    /// <param name="versions">The watch state of each version, null where there is none.</param>
    /// <returns>The flag, null when no version was ever played.</returns>
    public static bool? Decide(IEnumerable<UserItemData?> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);

        var winner = versions
            .OfType<UserItemData>()
            .Where(data => data.Played || data.LastPlayedDate is not null || data.PlaybackPositionTicks > 0 || data.PlayCount > 0)
            .OrderByDescending(data => data.LastPlayedDate ?? DateTime.MinValue)
            .ThenByDescending(data => data.Played)
            .FirstOrDefault();
        return winner?.Played;
    }
}
