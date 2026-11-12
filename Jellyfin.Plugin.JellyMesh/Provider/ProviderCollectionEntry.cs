namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// A shared film and the collection it is in.
/// </summary>
/// <param name="Id">The film.</param>
/// <param name="Name">The name of the collection, trimmed.</param>
/// <param name="TmdbId">The TMDb ID of the collection, digits only, if it has one.</param>
public sealed record ProviderCollectionEntry(string Id, string Name, string? TmdbId);
