namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// A chapter of a catalog entry.
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="StartPositionTicks">Where it starts.</param>
public sealed record IndexedChapter(string? Name, long StartPositionTicks);
