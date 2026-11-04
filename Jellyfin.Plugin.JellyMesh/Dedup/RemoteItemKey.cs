namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// One item on one remote server.
/// </summary>
/// <param name="ServerId">The plugin's ID for the server.</param>
/// <param name="ItemId">The item ID on that server.</param>
public sealed record RemoteItemKey(string ServerId, string ItemId);
