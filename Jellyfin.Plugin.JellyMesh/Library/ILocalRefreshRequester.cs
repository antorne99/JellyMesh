using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Asks Jellyfin to read the metadata of new local films that have no IDs yet (decision 214), and remembers when, in
/// memory only (Q246).
/// </summary>
public interface ILocalRefreshRequester
{
    /// <summary>
    /// Queues a metadata refresh of each item not asked for before.
    /// </summary>
    /// <param name="itemIds">The local items, all from the local catalog.</param>
    void Request(IReadOnlyCollection<Guid> itemIds);

    /// <summary>
    /// Tells whether the item was asked for at least one incremental sync interval ago, so that its merge cases stop
    /// waiting for a refresh that may never come (Q243).
    /// </summary>
    /// <param name="itemId">The local item.</param>
    /// <returns>True when the wait is over.</returns>
    bool IsOverdue(Guid itemId);
}
