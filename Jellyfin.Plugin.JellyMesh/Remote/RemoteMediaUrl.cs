namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// A <c>MediaUrl</c> of a remote catalog, such as a trailer.
/// </summary>
/// <param name="Url">The address, untrusted.</param>
public sealed record RemoteMediaUrl(string? Url);
