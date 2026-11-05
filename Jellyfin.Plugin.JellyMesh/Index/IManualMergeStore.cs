using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// The merges made by the administrator (decision 20). They outlive restarts and syncs.
/// </summary>
public interface IManualMergeStore
{
    /// <summary>
    /// Gets the merges. The copy never changes.
    /// </summary>
    IReadOnlyList<ManualMerge> Merges { get; }

    /// <summary>
    /// Gets a value indicating whether the file could not be read, so merges made earlier are not applied.
    /// </summary>
    bool IsDamaged { get; }

    /// <summary>
    /// Changes the merges. The change is saved first and only then made visible.
    /// </summary>
    /// <param name="change">Makes the new list from the current one.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task UpdateAsync(Func<IReadOnlyList<ManualMerge>, IReadOnlyList<ManualMerge>> change, CancellationToken cancellationToken);
}
