namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// A film of a JellyMesh remote and the collection it is in, as the remote says. Every field is untrusted.
/// </summary>
/// <param name="Id">The film.</param>
/// <param name="Name">The name of the collection.</param>
/// <param name="TmdbId">The TMDb ID of the collection.</param>
public sealed record RemoteCollectionEntry(string? Id, string? Name, string? TmdbId);
