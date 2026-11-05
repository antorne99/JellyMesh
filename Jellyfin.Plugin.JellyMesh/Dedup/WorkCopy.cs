namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// A film copy of a work.
/// </summary>
/// <param name="Item">The item.</param>
/// <param name="IsLocal">Whether it is in the local library.</param>
public sealed record WorkCopy(CatalogItem Item, bool IsLocal);
