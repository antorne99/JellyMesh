using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The sync the automatic migration runs before its scan, so that the server folders hold the new stubs (§10 row 420).
/// </summary>
public interface IMigrationSync
{
    /// <summary>
    /// Runs a sync of the configured servers and waits for it, or for the one already running. A server that fails is
    /// only marked, as in any sync, and its titles wait.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when no sync runs.</returns>
    Task SyncAsync(CancellationToken cancellationToken);
}
