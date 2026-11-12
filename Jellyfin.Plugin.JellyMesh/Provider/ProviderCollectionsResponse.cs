using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// A page of the shared films of a library that are in a collection.
/// </summary>
/// <param name="Items">The films with their collections.</param>
/// <param name="TotalRecordCount">The films of the whole query.</param>
public sealed record ProviderCollectionsResponse(IReadOnlyList<ProviderCollectionEntry> Items, int TotalRecordCount);
