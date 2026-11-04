using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// The catalogs of the remote servers, persisted in the plugin data folder (plan §3.1).
/// </summary>
public interface IFederationIndex
{
    /// <summary>
    /// Gets the current index. The copy never changes, so it can be read without locks.
    /// </summary>
    IndexSnapshot Snapshot { get; }

    /// <summary>
    /// Replaces the entries of a server with a complete read of its catalog.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="items">Every entry of the server.</param>
    /// <param name="syncedUtc">When the read ended.</param>
    /// <param name="mark">Where the next incremental read starts from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the change was saved. If not, the index stays as it was.</returns>
    Task<bool> ReplaceServerAsync(Guid serverId, IReadOnlyList<IndexedItem> items, DateTimeOffset syncedUtc, SyncMark? mark, CancellationToken cancellationToken);

    /// <summary>
    /// Adds or updates the entries an incremental read found. Nothing leaves the index, because such a read cannot tell
    /// what is gone. The state does not change either: only <see cref="ReplaceServerAsync"/> makes a server authoritative,
    /// so a server that is not one is left alone.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="changed">The entries that changed.</param>
    /// <param name="syncedUtc">When the read ended.</param>
    /// <param name="mark">Where the next incremental read starts from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the change was saved. If not, the index stays as it was.</returns>
    Task<bool> MergeServerAsync(Guid serverId, IReadOnlyList<IndexedItem> changed, DateTimeOffset syncedUtc, SyncMark mark, CancellationToken cancellationToken);

    /// <summary>
    /// Records a failed sync. The entries of the server are kept, and it stops being authoritative.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="failure">Why the sync failed.</param>
    /// <param name="attemptUtc">When it failed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the change was saved. If not, the index stays as it was.</returns>
    Task<bool> MarkServerFailedAsync(Guid serverId, RemoteFailure failure, DateTimeOffset attemptUtc, CancellationToken cancellationToken);
}
