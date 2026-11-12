using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The old ids of the libraries the plugin renamed since the server started, which stock still serves until a restart
/// (§10 row 86). Kept in memory only, since the restart is what closes the gap.
/// </summary>
public sealed class RetiredLibraryIds
{
    private readonly ConcurrentDictionary<Guid, byte> _ids = new();

    /// <summary>Gets a value indicating whether no id is retired.</summary>
    public bool IsEmpty => _ids.IsEmpty;

    /// <summary>Records the old id of a renamed library.</summary>
    /// <param name="id">The old id.</param>
    public void Retire(Guid id) => _ids.TryAdd(id, 0);

    /// <summary>Takes back an id a library holds again, as after a rename back to an earlier name.</summary>
    /// <param name="id">The id of the renamed library.</param>
    public void Restore(Guid id) => _ids.TryRemove(id, out _);

    /// <summary>Tells whether an id is retired.</summary>
    /// <param name="id">The id.</param>
    /// <returns>True when it is.</returns>
    public bool Contains(Guid id) => _ids.ContainsKey(id);
}
