namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// One playable version on one remote server.
/// </summary>
/// <param name="ServerId">The plugin's ID for the server.</param>
/// <param name="ItemId">The item ID on that server.</param>
/// <param name="MediaSourceId">The media source ID of the version.</param>
/// <param name="Quality">The quality of the version.</param>
public sealed record RemoteSource(string ServerId, string ItemId, string MediaSourceId, SourceQuality Quality);
