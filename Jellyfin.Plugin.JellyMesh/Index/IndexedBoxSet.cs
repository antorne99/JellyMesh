namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// The collection (BoxSet) a film belongs to on its server. The name is untrusted text.
/// </summary>
/// <param name="Name">The name, which Jellyfin matches exactly.</param>
/// <param name="TmdbId">The TMDb ID of the collection, digits only, if the server has one.</param>
public sealed record IndexedBoxSet(string Name, string? TmdbId);
