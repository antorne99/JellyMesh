using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Renames a library with the steps of the dashboard, which a plugin cannot call (spec §3.3, Q38).
/// </summary>
public interface ILibraryRenamer
{
    /// <summary>
    /// Gets a value indicating whether a rename left user lists that still name an old id (§10 row 290).
    /// </summary>
    bool HasPending { get; }

    /// <summary>
    /// Stops the library monitor, moves the library folder and rewrites the library id in each user's lists; the home
    /// lists only where the server has view ids (12.2). The caller queues the scan, which starts the monitor again.
    /// Before the move, who blocks the old id also blocks the new one, and the rename is recorded until every user is
    /// rewritten.
    /// </summary>
    /// <param name="name">The current name.</param>
    /// <param name="itemId">The current library id, if known.</param>
    /// <param name="type">The library type.</param>
    /// <param name="newName">The new name, a valid file name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new library id, or null when the library is not there, the new name is taken or is the same.</returns>
    Task<Guid?> RenameAsync(string name, Guid? itemId, CollectionType? type, string newName, CancellationToken cancellationToken);

    /// <summary>
    /// Keeps what each user can see when libraries take the titles of an old one: who blocks the old one blocks them, and
    /// who has access to it by id, not to all libraries, gets them. Only the per-id lists change, as on a rename (§10 row 438).
    /// </summary>
    /// <param name="access">For each old library id, the ids of the libraries that take its titles.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that fails when a user cannot be saved.</returns>
    Task CarryAccessAsync(IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> access, CancellationToken cancellationToken);

    /// <summary>
    /// Finishes the recorded renames: the second move of a change of case, then the users left with an old id.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task ResumeAsync(CancellationToken cancellationToken);
}
