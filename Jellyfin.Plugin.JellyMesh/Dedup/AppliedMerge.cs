using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// What a <see cref="ManualMerge"/> found in a catalog.
/// </summary>
/// <param name="Merge">The merge.</param>
/// <param name="WorkKey">The work it landed on, null if none of its copies is in the catalog.</param>
/// <param name="IsInEffect">
/// Whether it joins at least two works. If not, its copies are missing or the IDs already make them one title.
/// </param>
/// <param name="Copies">Its copies, sorted, each with the item found for it.</param>
public sealed record AppliedMerge(
    ManualMerge Merge,
    string? WorkKey,
    bool IsInEffect,
    IReadOnlyList<AppliedCopy> Copies);
