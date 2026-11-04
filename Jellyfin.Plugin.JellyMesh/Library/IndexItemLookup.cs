using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Index;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Finds catalog entries by server and item, in one index snapshot.
/// </summary>
public sealed class IndexItemLookup
{
    private readonly Dictionary<(Guid ServerId, string ItemId), IndexedItem> _items;

    private IndexItemLookup(Dictionary<(Guid ServerId, string ItemId), IndexedItem> items, IndexSnapshot snapshot)
    {
        _items = items;
        Snapshot = snapshot;
    }

    /// <summary>
    /// Gets the snapshot this lookup was built from.
    /// </summary>
    public IndexSnapshot Snapshot { get; }

    /// <summary>
    /// Builds the lookup of a snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The lookup.</returns>
    public static IndexItemLookup Build(IndexSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var items = new Dictionary<(Guid ServerId, string ItemId), IndexedItem>();
        foreach (var (server, item) in snapshot.Servers.SelectMany(catalog => catalog.Items.Select(item => (catalog.ServerId, item))))
        {
            items.TryAdd((server, item.ItemId), item);
        }

        return new IndexItemLookup(items, snapshot);
    }

    /// <summary>
    /// Finds an entry.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="itemId">The item on the server.</param>
    /// <returns>The entry, null if the index does not have it.</returns>
    public IndexedItem? Find(Guid serverId, string itemId) => _items.GetValueOrDefault((serverId, itemId));
}
