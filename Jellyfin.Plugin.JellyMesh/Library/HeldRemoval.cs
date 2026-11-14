namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A removed server whose removal waits until the watch state of its titles is saved (row 416). Holds its name only.
/// </summary>
/// <param name="Name">The name the server had, null when this build never saw it.</param>
public sealed record HeldRemoval(string? Name);
