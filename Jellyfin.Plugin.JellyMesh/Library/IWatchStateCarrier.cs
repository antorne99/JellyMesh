using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Carries each user's watch state from old items to the new items of their titles, for the migration and a server
/// rename (Q38, Q41, Q47, decisions 149-155, 179, 187).
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
    /// forgotten here: the report names the old items the caller may forget once removed (decisions 179, 187).
    /// </summary>
    /// <param name="moves">
    /// The old items and their new items. Versions in one registry group must be linked first, with their primary among
    /// them; stubs in no group or in different groups take the state each on its own (D3).
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was done with each move.</returns>
    Task<WatchStateCarryReport> CarryAsync(IReadOnlyList<WatchStateMove> moves, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets the carry records of old items the caller has just removed (decision 187). Nothing is forgotten while the
    /// record cannot be read (decision 176) or no user is found.
    /// </summary>
    /// <remarks>
    /// A caller that sets <see cref="WatchStateMove.RemovesOldItem"/> holds <see cref="StubRegistry.EnterAsync"/>, the lock
    /// of the manual merge, once through the carry, the removal and this call, so two runs never overlap (decision 187).
    /// </remarks>
    /// <param name="oldItemIds">Old items from <see cref="WatchStateCarryReport.ForgettableOldItemIds"/> that are removed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the records are forgotten and saved, or there was nothing to forget.</returns>
    Task<bool> ForgetRemovedAsync(IReadOnlyCollection<Guid> oldItemIds, CancellationToken cancellationToken);
}
