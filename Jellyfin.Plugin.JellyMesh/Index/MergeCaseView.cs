using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Dedup;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// Film titles shown apart that the administrator can merge.
/// </summary>
/// <param name="Kind">Why they stayed apart.</param>
/// <param name="Titles">The titles.</param>
public sealed record MergeCaseView(DedupIssueKind Kind, IReadOnlyList<MergeTitleView> Titles);
