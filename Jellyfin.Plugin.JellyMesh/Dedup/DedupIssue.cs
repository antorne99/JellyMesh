using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// A doubtful match, for the caller to log (plan §9).
/// </summary>
/// <param name="Kind">The kind of issue.</param>
/// <param name="WorkKeys">The work keys involved, sorted.</param>
public sealed record DedupIssue(DedupIssueKind Kind, IReadOnlyList<string> WorkKeys);
