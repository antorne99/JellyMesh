using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What a run of the watch state carry did, move by move, so that the caller knows what to run again.
/// </summary>
/// <param name="Moves">The outcome of each move, in the order given.</param>
public sealed record WatchStateCarryReport(IReadOnlyList<WatchStateMoveOutcome> Moves)
{
    /// <summary>Gets the number of moves done for every user.</summary>
    public int Carried => Moves.Count(move => move.Status == WatchStateMoveStatus.Carried);

    /// <summary>
    /// Gets the number of moves not done for every user. Stubs never linked because their ranges differ (D3) are not
    /// among them: they take the state each on its own.
    /// </summary>
    public int Skipped => Moves.Count - Carried;

    /// <summary>
    /// Gets the old items whose moves all remove them and are done for every user. Once the caller has removed them it
    /// passes them to <see cref="IWatchStateCarrier.ForgetRemovedAsync"/> (decisions 179, 187).
    /// </summary>
    public IReadOnlyList<Guid> ForgettableOldItemIds { get; init; } = [];
}
