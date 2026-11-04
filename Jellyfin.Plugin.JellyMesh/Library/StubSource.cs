using System;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A remote source of a stub. Holds no address.
/// </summary>
/// <param name="ServerId">The server.</param>
/// <param name="ItemId">The item on the server.</param>
/// <param name="MediaSourceId">The version of the item.</param>
public sealed record StubSource(Guid ServerId, string ItemId, string MediaSourceId)
{
    /// <summary>Gets the remote library the item was read from.</summary>
    public string? LibraryId { get; init; }
}
