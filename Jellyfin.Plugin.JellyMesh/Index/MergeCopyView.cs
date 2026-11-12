using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Dedup;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// A film or series copy as the administrator sees it. Holds no address.
/// </summary>
/// <param name="Copy">The name of the copy, which is also what a merge stores.</param>
/// <param name="ServerName">The name of the server, null for a local copy or a server removed since.</param>
/// <param name="RunTimeTicks">The duration, null if unknown or the copy cannot be found.</param>
/// <param name="IsFound">Whether the copy is in the catalog now.</param>
public sealed record MergeCopyView(MergedCopy Copy, string? ServerName, long? RunTimeTicks, bool IsFound)
{
    /// <summary>
    /// Gets the versions of the copy with their quality, best first. Empty for a local copy, whose quality is not read
    /// because it wins anyway, for a series, whose quality is decided per episode, and for a copy that cannot be found.
    /// </summary>
    public IReadOnlyList<MediaVersion> Versions { get; init; } = [];
}
