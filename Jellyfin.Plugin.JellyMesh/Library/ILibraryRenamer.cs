using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;

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
    /// Keeps what each user can see when new libraries take the titles of old ones: who blocks an old one blocks the new
    /// ones that hold any of its folders, and who has by id, not through all libraries, old libraries holding every folder
    /// of a new one gets it. Only the per-id lists change, as on a rename, and never for an adopted library (§10 rows 438, 440).
    /// </summary>
    /// <param name="carries">The new libraries, each with the old libraries that held its folders.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The users who saw titles of a new library through an old one and do not see the new one.</returns>
    Task<IReadOnlyList<AccessLoss>> CarryAccessAsync(IReadOnlyList<AccessCarry> carries, CancellationToken cancellationToken);

    /// <summary>
    /// Before a scan shows the new libraries, who blocks an old one also blocks the new ones that hold any of its folders.
    /// Nothing is given and an adopted library is never written; a library with no id yet is found by its name.
    /// </summary>
    /// <param name="carries">The new libraries, each with the old libraries that held its folders.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task BlockAsync(IReadOnlyList<AccessCarry> carries, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the id of a library the plugin deletes from one per-id list of each user, in any form, so a library made
    /// again with its name is not given or blocked by an old entry; a library with no id is found by its name.
    /// </summary>
    /// <param name="name">The library name.</param>
    /// <param name="itemId">The library id, if known.</param>
    /// <param name="list">The list, enabled or blocked folders.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task RemoveFromUsersAsync(string name, Guid? itemId, PreferenceKind list, CancellationToken cancellationToken);

    /// <summary>
    /// Tells, changing nothing, which users would lose titles if the access were carried to the new libraries.
    /// </summary>
    /// <param name="carries">The new libraries, each with the old libraries that held its folders.</param>
    /// <returns>The users who saw titles of a new library through an old one and would not see the new one.</returns>
    IReadOnlyList<AccessLoss> LosingAccess(IReadOnlyList<AccessCarry> carries);

    /// <summary>
    /// Finishes the recorded renames: the second move of a change of case, then the users left with an old id.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task ResumeAsync(CancellationToken cancellationToken);
}
