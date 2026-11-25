using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Renames a server with its stubs and automatic libraries (Q38).
/// </summary>
public interface IServerRenamer
{
    /// <summary>
    /// Gets a value indicating whether a library the plugin renamed keeps its old id until a restart (§10 row 86).
    /// </summary>
    bool NeedsRestart { get; }

    /// <summary>
    /// Gets the state of the rename of a server.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>The state.</returns>
    ServerRenameState GetState(Guid serverId);

    /// <summary>
    /// Renames a server, then in the background moves its stubs to the new label, renames its automatic libraries, scans
    /// and links the versions. The same name runs the steps again, which finishes a rename that failed.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="name">The new name.</param>
    /// <param name="confirmed">Whether the admin confirmed after the warning.</param>
    /// <returns>The answer.</returns>
    ServerRenameRequest Rename(Guid serverId, string? name, bool confirmed);

    /// <summary>
    /// Renames under its current name each server whose saved label is over the byte limit, one after another, with the
    /// steps of a rename (row 564), once the libraries are ready. A server whose rename is refused or keeps its label is left
    /// for the next start. It is called only at a start with no title in the older layouts (row 565).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many servers got a label within the limit.</returns>
    Task<int> RenameLongLabelsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Drops the stub moves of a server that wait for a scan, and deletes their old files (row 288).
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>Cancelled, CancelFailed when an old file stays, UnknownServer, or Busy while a rename runs.</returns>
    Task<ServerRenameOutcome> CancelPendingAsync(Guid serverId);
}
