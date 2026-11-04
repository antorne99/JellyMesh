using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// A page of the catalog, in the shape of <c>/Items</c>.
/// </summary>
/// <param name="Items">The items.</param>
/// <param name="TotalRecordCount">The items of the whole query.</param>
/// <param name="Quality">The quality of each version, by media source.</param>
public sealed record ProviderItemsResponse(IReadOnlyList<BaseItemDto> Items, int TotalRecordCount, IReadOnlyDictionary<string, RemoteQuality> Quality);
