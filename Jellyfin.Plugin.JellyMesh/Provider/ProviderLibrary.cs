namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// A library.
/// </summary>
/// <param name="Id">The identifier.</param>
/// <param name="Name">The name.</param>
/// <param name="CollectionType">The collection type, as <c>/UserViews</c> says it.</param>
public sealed record ProviderLibrary(string Id, string Name, string CollectionType);
