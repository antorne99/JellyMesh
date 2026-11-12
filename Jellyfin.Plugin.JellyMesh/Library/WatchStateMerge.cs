using System;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Hands the user data of a stub over to the local copy that replaces it (decision 2).
/// </summary>
public static class WatchStateMerge
{
    /// <summary>
    /// Brings what the stub knows into the data of the local item. The most recent <c>LastPlayedDate</c> wins for the
    /// whole watch state, the rule Jellyfin itself uses when it reattaches rows. A favorite stays one, and a rating only
    /// fills a gap.
    /// </summary>
    /// <param name="stub">The data of the stub.</param>
    /// <param name="local">The data of the local item, changed in place.</param>
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

    /// <summary>
    /// Keeps a favorite set on a stub series row, which is all a series row holds that the local one can use.
    /// </summary>
    /// <param name="stub">The data of the stub series.</param>
    /// <param name="local">The data of the local series, changed in place.</param>
    /// <returns>Whether <paramref name="local"/> changed.</returns>
    public static bool MergeFavorite(UserItemData stub, UserItemData local)
    {
        ArgumentNullException.ThrowIfNull(stub);
        ArgumentNullException.ThrowIfNull(local);

        if (!stub.IsFavorite || local.IsFavorite)
        {
            return false;
        }

        local.IsFavorite = true;
        return true;
    }

    /// <summary>
    /// Tells whether one row's watch state is newer than another's, by the rule of <see cref="Merge"/>.
    /// </summary>
    /// <param name="from">The row that would hand its state over.</param>
    /// <param name="to">The row that would take it.</param>
    /// <returns>Whether <paramref name="from"/> has watch state and it is newer.</returns>
    public static bool IsNewer(UserItemData from, UserItemData to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        return StubIsNewer(from, to);
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
