namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// A copy of a merge in a catalog.
/// </summary>
/// <param name="Copy">The copy.</param>
/// <param name="Item">The item, null if it cannot be found.</param>
public sealed record AppliedCopy(MergedCopy Copy, CatalogItem? Item);
