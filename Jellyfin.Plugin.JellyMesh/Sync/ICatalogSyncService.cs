using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// Sync of the chosen libraries into the index. Only one sync of any kind runs at a time.
/// </summary>
public interface ICatalogSyncService
{
    /// <summary>
    /// Starts a full sync in the background.
    /// </summary>
    /// <returns>Whether it started.</returns>
    SyncStartResult TryStart();

    /// <summary>
    /// Runs a full sync and waits for it. This is the reconciliation: what a complete read no longer finds leaves the
    /// index, and a server that fails keeps its entries.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether it ran. If a sync is running this one does not, and nothing waits for it.</returns>
    Task<SyncStartResult> RunAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads what changed since the last read of each server, and waits for it. Nothing leaves the index. A server whose
    /// entries are not a complete read, or whose chosen libraries changed, gets a full read instead, which is also how
    /// a server that was offline recovers.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether it ran. If a sync is running this one does not, and nothing waits for it.</returns>
    Task<SyncStartResult> RunIncrementalAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the state of the sync.
    /// </summary>
    /// <returns>The state.</returns>
    SyncStatus GetStatus();
}
