using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// What a remote answered to a read of the media segments of one item.
/// </summary>
/// <param name="Segments">The segments, empty when it has none or refused the item.</param>
/// <param name="ItemRefused">Whether it refused that item alone with a client error, such as not found, which says
/// nothing about the server.</param>
public sealed record RemoteSegmentRead(IReadOnlyList<RemoteSegment> Segments, bool ItemRefused = false)
{
    /// <summary>
    /// Gets the answer for an item the remote refused alone.
    /// </summary>
    public static RemoteSegmentRead Refused { get; } = new([], true);
}
