using System;
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
    /// Stops the library monitor, moves the library folder and rewrites the library id in each user's lists; the home
    /// lists only where the server has view ids (12.2). The caller queues the scan, which starts the monitor again.
    /// </summary>
    /// <param name="name">The current name.</param>
    /// <param name="itemId">The current library id, if known.</param>
    /// <param name="type">The library type.</param>
    /// <param name="newName">The new name, a valid file name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new library id, or null when the library is not there or the new name is taken.</returns>
    Task<Guid?> RenameAsync(string name, Guid? itemId, CollectionType? type, string newName, CancellationToken cancellationToken);
}
