using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Creates, follows and deletes the Film and Serie libraries the plugin keeps for a server when its switch is on.
/// </summary>
public interface IServerLibraryManager
{
    /// <summary>
    /// Gets what the page shows for a server.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>The state, or the empty state for an unknown server.</returns>
    AutomaticLibrariesState GetState(Guid serverId);

    /// <summary>
    /// Turns the switch on and creates in the background the libraries the registry lacks.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>The answer.</returns>
    AutomaticLibrariesRequest Enable(Guid serverId);

    /// <summary>
    /// Deletes in the background the libraries of the registry and turns the switch off, once confirmed.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="confirmed">Whether the admin confirmed the deletion.</param>
    /// <returns>The answer.</returns>
    AutomaticLibrariesRequest Disable(Guid serverId, bool confirmed);

    /// <summary>
    /// Compares the registry with the libraries of the server, following renames and dropping what changed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when done, or at once when a creation or a deletion is running.</returns>
    Task ReconcileAsync(CancellationToken cancellationToken);
}
