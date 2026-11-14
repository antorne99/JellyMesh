namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// What became of a request to merge titles.
/// </summary>
public enum ManualMergeOutcome
{
    /// <summary>The titles are merged.</summary>
    Merged,

    /// <summary>Fewer than two titles were chosen.</summary>
    TooFewTitles,

    /// <summary>A title is no longer one of a same case: the catalog changed since the list was read.</summary>
    ListChanged,

    /// <summary>Two of the film titles have a local copy, and local films cannot become one.</summary>
    TwoLocalTitles,

    /// <summary>Two of the series titles have a local copy, and local series cannot become one.</summary>
    TwoLocalSeries,
}
