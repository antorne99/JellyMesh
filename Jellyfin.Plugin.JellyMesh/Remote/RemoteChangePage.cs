using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// A page of the change feed of a JellyMesh remote.
/// </summary>
/// <param name="Reset">Whether the cursor cannot be continued, so everything has to be read.</param>
/// <param name="Cursor">Where the next read starts from.</param>
/// <param name="HasMore">Whether more changes follow.</param>
/// <param name="Changes">The items that changed.</param>
public sealed record RemoteChangePage(bool Reset, string Cursor, bool HasMore, IReadOnlyList<RemoteChange> Changes)
{
    /// <summary>Gets the size of the answer, which counts towards what one read may bring into memory.</summary>
    public long ByteCount { get; init; }
}
