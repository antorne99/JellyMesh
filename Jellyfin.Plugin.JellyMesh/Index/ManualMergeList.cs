using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// What the configuration page shows about manual merges.
/// </summary>
/// <param name="Cases">The titles that can be merged.</param>
/// <param name="Merges">The merges made.</param>
/// <param name="IsStoreDamaged">Whether earlier merges could not be read.</param>
public sealed record ManualMergeList(IReadOnlyList<MergeCaseView> Cases, IReadOnlyList<ManualMergeView> Merges, bool IsStoreDamaged);
