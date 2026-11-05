using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// A choice of the administrator: these film copies are one title (decision 20).
/// </summary>
/// <remarks>
/// It joins the works its copies belong to, so whatever else belongs to those works comes along. A copy that cannot be found
/// joins nothing, and the merge waits for it: with fewer than two works found it has no effect, and it applies again when
/// the copy is back. The result is the same whatever the order of the merges and of their copies.
/// </remarks>
public sealed record ManualMerge
{
    /// <summary>Gets the ID of the merge.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets when it was made.</summary>
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Gets the copies.</summary>
    public required IReadOnlyList<MergedCopy> Copies { get; init; }
}
