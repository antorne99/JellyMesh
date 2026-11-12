using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// Film or series titles shown apart that may be one, for the administrator to merge (decisions 20 and 51). Doubtful
/// matches that share a title make one case, so that a title appears once.
/// </summary>
/// <param name="Kind">Why they stayed apart. With both reasons, the one of contradicting IDs.</param>
/// <param name="Works">The titles, sorted by key.</param>
public sealed record MergeCase(DedupIssueKind Kind, IReadOnlyList<MergeCaseWork> Works)
{
    /// <summary>Gets whether the titles are films or series; a series copy is shown by one of its episodes.</summary>
    public ContentKind ContentKind { get; init; }
}
