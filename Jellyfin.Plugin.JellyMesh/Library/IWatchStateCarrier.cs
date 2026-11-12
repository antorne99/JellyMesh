using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Carries each user's watch state from old items to the new versions of their titles, for the migration and a server
/// rename (Q38, Q41, Q47).
/// </summary>
public interface IWatchStateCarrier
{
    /// <summary>
    /// Carries the watch state of each move for every user. Running it again changes nothing already carried.
    /// </summary>
    /// <param name="moves">
    /// The old items and their new versions. Several versions must be linked first, with their primary among them.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was carried and what was left.</returns>
    WatchStateCarryReport Carry(IReadOnlyList<WatchStateMove> moves, CancellationToken cancellationToken);
}
