using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// What the configuration page shows about manual merges.
/// </summary>
/// <param name="Cases">The film titles that can be merged.</param>
/// <param name="Merges">The merges made, of films and series.</param>
/// <param name="IsStoreDamaged">Whether earlier merges could not be read.</param>
public sealed record ManualMergeList(IReadOnlyList<MergeCaseView> Cases, IReadOnlyList<ManualMergeView> Merges, bool IsStoreDamaged)
{
    /// <summary>
    /// Gets the series titles that can be merged (decision 51), apart from the films so that a page that only knows films
    /// does not show them as films.
    /// </summary>
    public IReadOnlyList<MergeCaseView> SeriesCases { get; init; } = [];
}
