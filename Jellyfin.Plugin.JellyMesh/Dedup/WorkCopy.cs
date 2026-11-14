namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// A film copy of a work, or a series copy shown by one of its episodes.
/// </summary>
/// <param name="Item">The film, or an episode of the series.</param>
/// <param name="IsLocal">Whether it is in the local library.</param>
public sealed record WorkCopy(CatalogItem Item, bool IsLocal);
