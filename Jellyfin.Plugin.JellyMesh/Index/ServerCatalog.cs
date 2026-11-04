using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// What the index holds about one server.
/// </summary>
public sealed record ServerCatalog
{
    /// <summary>Gets the plugin's ID for the server.</summary>
    public required Guid ServerId { get; init; }

    /// <summary>Gets how far the entries can be trusted.</summary>
    public required ServerSyncState State { get; init; }

    /// <summary>Gets why the last sync failed, null if it did not.</summary>
    public RemoteFailure? LastFailure { get; init; }

    /// <summary>Gets when a sync last ended.</summary>
    public DateTimeOffset? LastAttemptUtc { get; init; }

    /// <summary>Gets when a sync last read the whole catalog. An incremental read does not move it.</summary>
    public DateTimeOffset? LastSuccessUtc { get; init; }

    /// <summary>Gets where the next incremental read starts from, null until a read that records it.</summary>
    public SyncMark? Mark { get; init; }

    /// <summary>Gets the entries.</summary>
    public IReadOnlyList<IndexedItem> Items { get; init; } = [];
}
