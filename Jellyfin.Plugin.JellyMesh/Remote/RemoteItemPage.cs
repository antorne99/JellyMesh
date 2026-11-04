using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// One page of <c>/Items</c>.
/// </summary>
/// <param name="Items">The items of the page.</param>
/// <param name="TotalRecordCount">The number of items matching the query across all pages.</param>
public sealed record RemoteItemPage(IReadOnlyList<RemoteItem> Items, int TotalRecordCount);
