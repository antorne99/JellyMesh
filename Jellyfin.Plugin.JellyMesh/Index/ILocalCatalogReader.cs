using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Dedup;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// Reads the local catalog for <see cref="DedupEngine"/>.
/// </summary>
public interface ILocalCatalogReader
{
    /// <summary>
    /// Reads the real local copies: no virtual item and none of the plugin's own stubs.
    /// </summary>
    /// <returns>The films and episodes.</returns>
    IReadOnlyList<CatalogItem> Read();
}
