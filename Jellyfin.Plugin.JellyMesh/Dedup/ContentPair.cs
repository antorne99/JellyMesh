namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// A saved copy and a current copy of the same content (Q412).
/// </summary>
/// <param name="Saved">The saved copy.</param>
/// <param name="Current">The current copy.</param>
public sealed record ContentPair(CatalogItem Saved, CatalogItem Current);
