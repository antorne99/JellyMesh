using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Carries each user's watch state from old items to the new items of their titles, for the migration and a server
/// rename (Q38, Q41, Q47, decisions 149-155).
/// </summary>
/// <remarks>
/// It writes the rows of every user, so only the system or an administrator route may call it, never a user request
/// (decision 96). Callers run it inside the sync lock, after the versions are linked.
/// </remarks>
public interface IWatchStateCarrier
{
    /// <summary>
    /// Carries the watch state of each move for every user. A carry done for a user and old item is repeated only after
    /// a newer watch of the old item, and then without favorite and rating (decision 155).
    /// </summary>
    /// <param name="moves">
    /// The old items and their new items. Versions in one registry group must be linked first, with their primary among
    /// them; stubs in no group or in different groups take the state each on its own (D3).
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was done with each move.</returns>
    Task<WatchStateCarryReport> CarryAsync(IReadOnlyList<WatchStateMove> moves, CancellationToken cancellationToken);
}
