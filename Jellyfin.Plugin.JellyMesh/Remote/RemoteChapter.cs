namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// A chapter of a remote item. A whitelist: the image path is left out.
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="StartPositionTicks">Where it starts.</param>
public sealed record RemoteChapter(string? Name, long StartPositionTicks);
