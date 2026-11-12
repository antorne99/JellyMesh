using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The federated folder and the paths to add by hand to the libraries.
/// </summary>
/// <param name="Path">The chosen folder, empty if none.</param>
/// <param name="MoviesPath">The path for the film library.</param>
/// <param name="SeriesPath">The path for the series library.</param>
/// <param name="Link">Whether the paths are linked.</param>
/// <param name="Movies">The link of the film path.</param>
/// <param name="Series">The link of the series path.</param>
/// <param name="SeriesGroupingOff">The libraries where a split series would not merge.</param>
/// <param name="FromEnvironment">Whether <c>JELLYMESH_FEDERATED_ROOT</c> sets the folder, so the page cannot change it.</param>
public sealed record FederatedFolderStatus(
    string Path,
    string MoviesPath,
    string SeriesPath,
    FederatedFolderLinkState Link,
    FederatedPathStatus Movies,
    FederatedPathStatus Series,
    IReadOnlyList<string> SeriesGroupingOff,
    bool FromEnvironment)
{
    /// <summary>Gets the manual migration from the flat layout, null when there is none to show.</summary>
    public LayoutMigrationStatus? Migration { get; init; }
}
