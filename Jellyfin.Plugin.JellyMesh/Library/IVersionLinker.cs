using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Jellyfin.Plugin.JellyMesh.Dedup;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Links the copies of a title as versions of one item.
/// </summary>
public interface IVersionLinker
{
    /// <summary>
    /// Links every group of versions as the registry has it, and saves nothing that is already linked.
    /// </summary>
    /// <param name="thorough">
    /// Whether this is the nightly run, which also looks up by path a stub whose item is not where its path says and
    /// detaches the stubs left alone in a group or out of every group, and moves to the primary every collection entry of
    /// an alternate.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task LinkAsync(bool thorough, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the stubs of every version group that holds one of these copies, as the registry has them now.
    /// </summary>
    /// <param name="copies">The copies, as a manual merge records them.</param>
    /// <returns>The stub IDs.</returns>
    IReadOnlyCollection<string> GroupedStubsOf(IReadOnlyCollection<MergedCopy> copies);

    /// <summary>
    /// Detaches from every item each of these stubs that is in no version group now, keeping only links between films.
    /// An undo uses it for the group it dissolved, so that other stubs out of a group wait for the nightly run (Q171).
    /// </summary>
    /// <param name="stubIds">The stubs.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task DetachAsync(IReadOnlyCollection<string> stubIds, CancellationToken cancellationToken);

    /// <summary>
    /// Detaches from every item each of these stubs about to be deleted, saving no metadata, so that Jellyfin hands no
    /// group over, and rewrites no <c>.nfo</c>, when it deletes them (Q276). The caller holds the stub registry lock.
    /// </summary>
    /// <param name="stubs">The stubs.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task DetachForRemovalAsync(IReadOnlyCollection<StubEntry> stubs, CancellationToken cancellationToken);
}
