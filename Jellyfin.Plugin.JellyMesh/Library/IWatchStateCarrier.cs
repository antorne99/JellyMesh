using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Carries each user's watch state from old items to the new items of their titles, for the migration and a server
/// rename (Q38, Q41, Q47, decisions 149-155, 179, 187, 203, 204).
/// </summary>
/// <remarks>
/// It writes the rows of every user, so only the system or an administrator route may call it, never a user request
/// (decision 96). Callers run it inside the sync lock, after the versions are linked.
/// </remarks>
public interface IWatchStateCarrier
{
    /// <summary>
    /// Carries the watch state of each move for every user. A carry done for a user and old item is repeated only after
    /// a newer watch of the old item, and then without favorite and rating (decision 155). A move is not counted as done
    /// when its record cannot be saved, and none runs while the record cannot be read (decision 176). Records are never
    /// forgotten here: the report names the old items the caller may ask to forget once removed (decisions 179, 187, 203).
    /// </summary>
    /// <param name="moves">
    /// The old items and their new items. Versions in one registry group must be linked first, with their primary among
    /// them; stubs in no group or in different groups take the state each on its own (D3).
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was done with each move.</returns>
    Task<WatchStateCarryReport> CarryAsync(IReadOnlyList<WatchStateMove> moves, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets the carry records of the old items no longer in the library: only the admin removes them, so the caller
    /// is not trusted (decisions 187, 204). An old item still there keeps its records until a later call, or a later
    /// carry, finds it gone. Nothing is forgotten while the record cannot be read (decision 176) or no user is found.
    /// </summary>
    /// <remarks>
    /// A caller that sets <see cref="WatchStateMove.RemovesOldItem"/> holds <see cref="StubRegistry.EnterAsync"/>, the lock
    /// of the manual merge, once through the carry, its removal of the old stubs and this call, so two runs never overlap
    /// (decision 187).
    /// </remarks>
    /// <param name="oldItemIds">Old items from <see cref="WatchStateCarryReport.ForgettableOldItemIds"/> that the caller removed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The old items whose records are forgotten and saved, or had none; the others keep their records. Empty when
    /// nothing could be forgotten.
    /// </returns>
    Task<IReadOnlyList<Guid>> ForgetRemovedAsync(IReadOnlyCollection<Guid> oldItemIds, CancellationToken cancellationToken);
}
