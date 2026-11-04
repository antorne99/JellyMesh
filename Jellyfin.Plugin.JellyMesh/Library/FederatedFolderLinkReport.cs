using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The links of the federated folder.
/// </summary>
/// <param name="Movies">The link of the film subfolder.</param>
/// <param name="Series">The link of the series subfolder.</param>
/// <param name="SeriesGroupingOff">
/// The series libraries holding the series subfolder with <c>EnableAutomaticSeriesGrouping</c> off, where a series split
/// across paths does not merge (Phase 0, point 2).
/// </param>
public sealed record FederatedFolderLinkReport(FederatedPathStatus Movies, FederatedPathStatus Series, IReadOnlyList<string> SeriesGroupingOff)
{
    /// <summary>
    /// Gets the report of a check that could not run.
    /// </summary>
    public static FederatedFolderLinkReport NotChecked { get; } = new(new(FederatedPathLink.NotChecked, []), new(FederatedPathLink.NotChecked, []), []);

    /// <summary>
    /// Gets the overall state.
    /// </summary>
    public FederatedFolderLinkState State => (Movies.Link, Series.Link) switch
    {
        (FederatedPathLink.NotChecked, _) or (_, FederatedPathLink.NotChecked) => FederatedFolderLinkState.NotChecked,
        (FederatedPathLink.Linked, FederatedPathLink.Linked) => FederatedFolderLinkState.Linked,
        _ => FederatedFolderLinkState.NotLinked
    };
}
