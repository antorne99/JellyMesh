using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// One page of the films of a library of a JellyMesh remote that are in a collection.
/// </summary>
/// <param name="Items">The films with their collections.</param>
/// <param name="TotalRecordCount">The films of the whole query.</param>
public sealed record RemoteCollectionPage(IReadOnlyList<RemoteCollectionEntry> Items, int TotalRecordCount)
{
    /// <summary>Gets the size of the answer, which counts towards what one read may bring into memory.</summary>
    public long ByteCount { get; init; }
}
