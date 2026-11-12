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
    /// Saves the carries recorded since the last save. A failure is logged, not thrown.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether every recorded carry is on disk.</returns>
    Task<bool> SaveAsync(CancellationToken cancellationToken);
}
