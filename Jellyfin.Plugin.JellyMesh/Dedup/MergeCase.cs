using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// Film titles shown apart that may be one, for the administrator to merge (decision 20). Doubtful matches that share a
/// title make one case, so that a title appears once.
/// </summary>
/// <param name="Kind">Why they stayed apart. With both reasons, the one of contradicting IDs.</param>
/// <param name="Works">The titles, sorted by key.</param>
public sealed record MergeCase(DedupIssueKind Kind, IReadOnlyList<MergeCaseWork> Works);
