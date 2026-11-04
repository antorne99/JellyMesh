using System;
using Jellyfin.Plugin.JellyMesh.Dedup;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// A remote source of a content identity. Holds no address.
/// </summary>
/// <param name="ServerId">The server.</param>
/// <param name="ServerName">The display name of the server.</param>
/// <param name="ServerState">How far the entries of the server can be trusted.</param>
/// <param name="ItemId">The item on the server.</param>
/// <param name="MediaSourceId">The version of the item.</param>
/// <param name="Title">The name of the item on the server.</param>
/// <param name="Quality">The quality of the version.</param>
/// <param name="LastSeenUtc">When the item was last seen.</param>
public sealed record ResolvedSource(
    Guid ServerId,
    string ServerName,
    ServerSyncState ServerState,
    string ItemId,
    string MediaSourceId,
    string? Title,
    SourceQuality Quality,
    DateTimeOffset LastSeenUtc);
