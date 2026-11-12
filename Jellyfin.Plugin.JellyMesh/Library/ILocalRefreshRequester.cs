using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Asks Jellyfin to read the metadata of new local films that have no IDs yet (decision 214).
/// </summary>
public interface ILocalRefreshRequester
{
    /// <summary>
    /// Queues a metadata refresh of each item not asked for before.
    /// </summary>
    /// <param name="itemIds">The local items, all from the local catalog.</param>
    void Request(IReadOnlyCollection<Guid> itemIds);
}
