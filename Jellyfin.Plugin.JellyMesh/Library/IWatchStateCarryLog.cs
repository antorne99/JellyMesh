using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The watch state carries done per user and old item, with the date the user last watched the old item then and the
/// operation they come from. A later run repeats a carry only after a newer watch, and never puts favorite or rating back
/// (decision 155).
/// </summary>
public interface IWatchStateCarryLog
{
    /// <summary>
    /// Reads the carries done if they could not be read before. Until they are read nothing is found, recorded or saved
    /// (decision 176).
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
    /// Finds the carry done for a user from an old item, with the operation it comes from (row 575).
    /// </summary>
    /// <param name="userId">The local user.</param>
    /// <param name="fromItemId">The old item.</param>
    /// <param name="lastPlayedDate">The user's last watch of the old item when it was carried, null if none.</param>
    /// <param name="operation">The operation of the carry, null for a record that names none it knows, as one made before row 575.</param>
    /// <returns>Whether the carry was done.</returns>
    bool TryFind(Guid userId, Guid fromItemId, out DateTime? lastPlayedDate, out WatchStateCarryOperation? operation);

    /// <summary>
    /// Records a carry just done and writes it at once; <see cref="SaveAsync"/> writes it when that fails. A record keeps
    /// the operation it had, except that a rename gives it its own, so a later round of the rename finds it (row 575).
    /// </summary>
    /// <param name="userId">The local user.</param>
    /// <param name="fromItemId">The old item.</param>
    /// <param name="lastPlayedDate">The user's last watch of the old item, null if none.</param>
    /// <param name="operation">The operation the carry comes from.</param>
    void Record(Guid userId, Guid fromItemId, DateTime? lastPlayedDate, WatchStateCarryOperation operation);

    /// <summary>
    /// Forgets the carries that match and writes that at once; <see cref="SaveAsync"/> writes it when that fails (decisions
    /// 165, 179).
    /// </summary>
    /// <param name="match">Tells, from the user and the old item, whether a carry goes.</param>
    /// <returns>The number of carries forgotten.</returns>
    int Forget(Func<Guid, Guid, bool> match);

    /// <summary>
    /// Saves the carries recorded or forgotten that could not be written yet. A failure is logged, not thrown.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether every recorded carry is in the database; false while it is not read.</returns>
    Task<bool> SaveAsync(CancellationToken cancellationToken);
}
