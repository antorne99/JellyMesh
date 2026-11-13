using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Moves the stubs of a renamed server to its new label, with their watch state (Q38).
/// </summary>
public interface IServerStubRenamer
{
    /// <summary>
    /// Tells whether stubs of a server still wait for their old items to go.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>True while a rename of the server is not done.</returns>
    bool IsPending(Guid serverId);

    /// <summary>
    /// Writes the stubs of a server under the label of its new name, and saves their new and old paths with the label
    /// in one step. A change of case keeps the label (row 84).
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="name">Its new name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many stubs moved.</returns>
    Task<int> RelabelAsync(Guid serverId, string name, CancellationToken cancellationToken);

    /// <summary>
    /// Carries the watch state of each renamed stub whose new item a scan has made, deletes its old files, and forgets
    /// the old items once the library no longer has them. Safe to run again: what is left waits for the next scan.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task FinishAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Drops the pending moves of a server and deletes their old files, except those a live stub has or carries from.
    /// The watch state not carried yet is lost (row 288).
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>False when an old file cannot be deleted, so its move stays pending.</returns>
    Task<bool> CancelAsync(Guid serverId, CancellationToken cancellationToken);
}
