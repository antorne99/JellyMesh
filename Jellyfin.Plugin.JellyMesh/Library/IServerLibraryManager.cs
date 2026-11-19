using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Creates, follows and deletes the Film and Serie libraries the plugin keeps for a server when its switch is on.
/// </summary>
public interface IServerLibraryManager
{
    /// <summary>
    /// Gets a value indicating whether a job, a deletion, a check or a rename may be changing the libraries or their
    /// registry, so that a library may not have its record yet.
    /// </summary>
    bool IsBusy { get; }

    /// <summary>
    /// Gets what the page shows for a server.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>The state, or the empty state for an unknown server.</returns>
    AutomaticLibrariesState GetState(Guid serverId);

    /// <summary>
    /// Turns the libraries of the server on, in the mode it has or else per type, and creates in the background those it lacks.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>The answer.</returns>
    AutomaticLibrariesRequest Enable(Guid serverId);

    /// <summary>
    /// Gives the server a library mode other than none and creates in the background the libraries it wants. The mode of
    /// a server that already has libraries changes only with the confirmation of the Edit form.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="mode">Per type or per remote library.</param>
    /// <returns>The answer.</returns>
    AutomaticLibrariesRequest Enable(Guid serverId, LibraryMode mode);

    /// <summary>
    /// Brings the libraries of the server to the mode saved for it in the background: those of the new mode are made or
    /// adopted and scanned first, then those of the old mode are deleted or released (§10 row 438).
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>The answer.</returns>
    AutomaticLibrariesRequest ApplyMode(Guid serverId);

    /// <summary>
    /// Tells what a change to the mode given would do to the libraries of the server, without changing anything.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="mode">The new mode.</param>
    /// <returns>The change, or null when the mode stays or the server is unknown.</returns>
    LibraryModeChange? PreviewModeChange(Guid serverId, LibraryMode mode);

    /// <summary>
    /// Deletes in the background the libraries of the registry and turns the switch off, once confirmed.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="confirmed">Whether the admin confirmed the deletion.</param>
    /// <returns>The answer.</returns>
    AutomaticLibrariesRequest Disable(Guid serverId, bool confirmed);

    /// <summary>
    /// Starts deleting in the background the libraries that removed servers left in their registry, with the checks of
    /// the switch-off and no confirmation (decision 140). A call while it runs makes it look once more.
    /// </summary>
    /// <returns>The job, which never fails.</returns>
    Task DeleteRemovedServerLibraries();

    /// <summary>
    /// Compares the registry with the libraries of the server, following renames and dropping what changed, then starts
    /// deleting what removed servers left, if anything.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when done, or at once when a creation or a deletion is running.</returns>
    Task ReconcileAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Waits, for at most a bounded time, until no library scan runs and every library has its id, when a check would
    /// follow a library (decision 213). It never fails but for a cancellation.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the libraries are ready or the wait ended.</returns>
    Task WaitReadyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records that a check of the libraries could not start, for every configured and removed server, so that the next
    /// one only records the libraries there and takes none for a renamed one (decision 185).
    /// </summary>
    void MarkFollowSkipped();

    /// <summary>
    /// Finishes the library renames left halfway, then gives the automatic libraries of a renamed server their new names. The caller queues the scan, which also starts the
    /// library monitor again.
    /// </summary>
    /// <param name="serverId">The server id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when every library that passes has its name and its users; false when one is left as it was.</returns>
    Task<bool> RenameAsync(Guid serverId, CancellationToken cancellationToken);

    /// <summary>
    /// Follows the registries and finds the automatic libraries in that same read, under the lock of the other library
    /// changes, for the placement of the servers linked later (§10 rows 384, 385).
    /// </summary>
    /// <returns>The libraries, or null when placement waits: a change runs or is left halfway, the libraries cannot be read,
    /// one has no id while the follow needs it, or one with no record may be the own library of a server.</returns>
    PlacementLibraries? FollowForPlacement();

    /// <summary>
    /// Finishes the library renames a stop left halfway, one at a time with the other library changes (§10 row 290).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task ResumeRenamesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets what the page shows for the shared libraries (§10 row 442).
    /// </summary>
    /// <returns>The state.</returns>
    SharedLibrariesState GetSharedState();

    /// <summary>
    /// Turns the shared libraries on, and creates or adopts in the background those missing, with no confirmation.
    /// </summary>
    /// <returns>The answer.</returns>
    AutomaticLibrariesRequest EnableShared();

    /// <summary>
    /// Deletes in the background the shared libraries the plugin made, releases the adopted ones and turns the option off,
    /// once confirmed.
    /// </summary>
    /// <param name="confirmed">Whether the admin confirmed the deletion.</param>
    /// <returns>The answer, with the libraries a confirmation would delete.</returns>
    AutomaticLibrariesRequest DisableShared(bool confirmed);
}
