using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Carries the watch state when a manual merge makes titles of one library one entity (decision 172, Q23, row 70).
/// </summary>
/// <remarks>
/// It writes the rows of every user, so only an administrator route may call it, never a user request (decision 96).
/// </remarks>
public interface IMergeWatchStateCarry
{
    /// <summary>
    /// Finds the items that show the copies as entities of their own, before the merge links them.
    /// </summary>
    /// <param name="copies">The copies of the titles being merged.</param>
    /// <returns>The entities, each by its item and its stub, if it is one.</returns>
    IReadOnlyList<MergeHead> FindHeads(IReadOnlyList<MergedCopy> copies);

    /// <summary>
    /// Once the merge is linked, carries the state of each entity that joined onto the primary of the title, when both are
    /// in one library: favorite, rating and play count to the primary, played to every version, the position stays.
    /// </summary>
    /// <param name="heads">The entities found before the merge.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the carry did, null when nothing joined in one library or the title is not linked as one.</returns>
    Task<WatchStateCarryReport?> CarryAsync(IReadOnlyList<MergeHead> heads, CancellationToken cancellationToken);
}
