using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// One page of <c>/Items</c>.
/// </summary>
/// <param name="Items">The items of the page.</param>
/// <param name="TotalRecordCount">The number of items matching the query across all pages.</param>
public sealed record RemoteItemPage(IReadOnlyList<RemoteItem> Items, int TotalRecordCount)
{
    /// <summary>Gets the size of the answer, which counts towards what one read may bring into memory.</summary>
    public long ByteCount { get; init; }
}
