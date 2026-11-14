using System;
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
    /// Waits until no sync of any kind runs.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when no sync runs.</returns>
    Task WaitIdleAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records that the administrator removed a server, then runs the removal: only such servers lose their catalogs and
    /// stubs. Waits for a materialization that is running. If the removal fails, the record is taken back.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="remove">What takes the server out of the configuration. False when there was no such server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the server was removed.</returns>
    /// <exception cref="System.Exception">The record or the removal could not be saved. The server stays.</exception>
    Task<bool> RemoveServerAsync(Guid serverId, Func<Task<bool>> remove, CancellationToken cancellationToken);

    /// <summary>
    /// Records as removed every server that left content but is missing from the settings, so that the next cleanup deletes
    /// it as after a removal. Waits for a materialization that is running.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many servers were marked now.</returns>
    /// <exception cref="System.IO.IOException">The marks could not be saved. Nothing is marked.</exception>
    Task<int> ForgetMissingServersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts deleting in the background what removed servers left: their catalogs and the stubs only they served. Titles
    /// that another server also has stay, and move to that server.
    /// </summary>
    /// <returns>The cleanup, which never fails.</returns>
    Task StartRemovalCleanup();

    /// <summary>
    /// Writes the folders of every configured server in the federated folder, so that a library can point at them before
    /// any sync (spec "cartelle per server" §3.1). An error is logged, not thrown.
    /// </summary>
    void PrepareServerFolders();

    /// <summary>
    /// Gets the state of the sync.
    /// </summary>
    /// <returns>The state.</returns>
    SyncStatus GetStatus();
}
