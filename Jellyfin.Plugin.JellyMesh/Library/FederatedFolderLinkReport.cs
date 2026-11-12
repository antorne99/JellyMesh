using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The links of the folders of each server inside the federated folder.
/// </summary>
/// <param name="Servers">The links of each server, in the order asked.</param>
/// <param name="Covering">The libraries over the federated folder or over a server folder, which break every link (Q45).</param>
/// <param name="SeriesSplit">Whether the series folders of the servers sit in different sets of libraries (Q46).</param>
public sealed record FederatedFolderLinkReport(IReadOnlyList<ServerFolderLink> Servers, IReadOnlyList<string> Covering, bool SeriesSplit)
{
    /// <summary>
    /// Gets the report of a check that could not run.
    /// </summary>
    public static FederatedFolderLinkReport NotChecked { get; } = new([], [], false) { IsChecked = false };

    /// <summary>
    /// Gets a value indicating whether the libraries could be read.
    /// </summary>
    public bool IsChecked { get; init; } = true;

    /// <summary>
    /// Gets the overall state: linked only when there are servers and both folders of each are linked.
    /// </summary>
    public FederatedFolderLinkState State => !IsChecked
        ? FederatedFolderLinkState.NotChecked
        : Servers.Count > 0 && Servers.All(server => server.Movies.Link == FederatedPathLink.Linked && server.Series.Link == FederatedPathLink.Linked)
            ? FederatedFolderLinkState.Linked
            : FederatedFolderLinkState.NotLinked;
}
