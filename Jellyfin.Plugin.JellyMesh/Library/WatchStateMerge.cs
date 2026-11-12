using System;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Hands the user data of a stub over to the stub of the same server that it was merged into (decision 2, Q23). Copies
/// that become versions keep their own, and only "played" is aligned when they are linked (Q42, D4).
/// </summary>
public static class WatchStateMerge
{
    /// <summary>
    /// Brings what the merged stub knows into the data of the stub that stays. The most recent <c>LastPlayedDate</c> wins
    /// for the whole watch state, the rule Jellyfin itself uses when it reattaches rows. A favorite stays one, and a rating
    /// only fills a gap.
    /// </summary>
    /// <param name="stub">The data of the merged stub.</param>
    /// <param name="local">The data of the stub that stays, changed in place.</param>
    /// <returns>Whether <paramref name="local"/> changed.</returns>
    public static bool Merge(UserItemData stub, UserItemData local)
    {
        ArgumentNullException.ThrowIfNull(stub);
        ArgumentNullException.ThrowIfNull(local);

        var changed = false;
        if (StubIsNewer(stub, local))
        {
            local.Played = stub.Played;
            local.PlayCount = stub.PlayCount;
            local.PlaybackPositionTicks = stub.PlaybackPositionTicks;
            local.LastPlayedDate = stub.LastPlayedDate;
            changed = true;
        }

        if (stub.IsFavorite && !local.IsFavorite)
        {
            local.IsFavorite = true;
            changed = true;
        }

        if (stub.Rating is not null && local.Rating is null)
        {
            local.Rating = stub.Rating;
            changed = true;
        }

        return changed;
    }

    private static bool HasWatchState(UserItemData data)
    {
        return data.Played || data.PlayCount > 0 || data.PlaybackPositionTicks > 0 || data.LastPlayedDate is not null;
    }

    private static bool StubIsNewer(UserItemData stub, UserItemData local)
    {
        if (!HasWatchState(stub))
        {
            return false;
        }

        if (!HasWatchState(local))
        {
            return true;
        }

        return stub.LastPlayedDate is { } stubDate && (local.LastPlayedDate is not { } localDate || stubDate > localDate);
    }
}
