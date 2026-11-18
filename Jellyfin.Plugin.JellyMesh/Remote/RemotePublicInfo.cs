namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// What a remote says of itself without a login: its name and its Server Id (row 433).
/// </summary>
/// <param name="ServerName">The name, cleaned and bounded; empty when the remote gives none. Untrusted text.</param>
/// <param name="ServerId">The Server Id as 32 lowercase digits, null when the answer holds no usable one.</param>
public sealed record RemotePublicInfo(string ServerName, string? ServerId);
