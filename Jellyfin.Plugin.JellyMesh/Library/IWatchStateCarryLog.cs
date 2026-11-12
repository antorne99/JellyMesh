using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The watch state carries done per user and old item, with the date the user last watched the old item then. A later
/// run repeats a carry only after a newer watch, and never puts favorite or rating back (decision 155).
/// </summary>
public interface IWatchStateCarryLog
{
    /// <summary>
    /// Reads the file if it could not be read before. Until it is read nothing is found, recorded or saved (decision 176).
    /// </summary>
    /// <returns>Whether the carries done are known.</returns>
    bool EnsureLoaded();

    /// <summary>
    /// Finds the carry done for a user from an old item.
    /// </summary>
    /// <param name="userId">The local user.</param>
    /// <param name="fromItemId">The old item.</param>
    /// <param name="lastPlayedDate">The user's last watch of the old item when it was carried, null if none.</param>
    /// <returns>Whether the carry was done.</returns>
    bool TryFind(Guid userId, Guid fromItemId, out DateTime? lastPlayedDate);

    /// <summary>
    /// Records in memory a carry just done; <see cref="SaveAsync"/> keeps it.
    /// </summary>
    /// <param name="userId">The local user.</param>
    /// <param name="fromItemId">The old item.</param>
    /// <param name="lastPlayedDate">The user's last watch of the old item, null if none.</param>
    void Record(Guid userId, Guid fromItemId, DateTime? lastPlayedDate);

    /// <summary>
    /// Forgets in memory the carries that match; <see cref="SaveAsync"/> keeps that (decision 165).
    /// </summary>
    /// <param name="match">Tells, from the user and the old item, whether a carry goes.</param>
    /// <returns>The number of carries forgotten.</returns>
    int Forget(Func<Guid, Guid, bool> match);

    /// <summary>
    /// Saves the carries recorded since the last save. A failure is logged, not thrown.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether every recorded carry is on disk; false while the file is not read.</returns>
    Task<bool> SaveAsync(CancellationToken cancellationToken);
}
