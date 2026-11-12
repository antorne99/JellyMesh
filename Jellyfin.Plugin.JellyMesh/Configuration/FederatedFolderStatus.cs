using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Library;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The federated folder and the folders of each server to add by hand to the libraries.
/// </summary>
/// <param name="Path">The chosen folder, empty if none.</param>
/// <param name="Link">Whether the folders of every server are linked.</param>
/// <param name="Servers">The folders of each server and their links.</param>
/// <param name="Covering">The libraries over the federated folder or over a server folder, which break every link.</param>
/// <param name="SeriesSplit">Whether the series folders of the servers sit in different sets of libraries.</param>
/// <param name="FromEnvironment">Whether <c>JELLYMESH_FEDERATED_ROOT</c> sets the folder, so the page cannot change it.</param>
public sealed record FederatedFolderStatus(
    string Path,
    FederatedFolderLinkState Link,
    IReadOnlyList<ServerFolderStatus> Servers,
    IReadOnlyList<string> Covering,
    bool SeriesSplit,
    bool FromEnvironment)
{
    /// <summary>Gets the manual migration from the flat layout, null when there is none to show.</summary>
    public LayoutMigrationStatus? Migration { get; init; }
}
