using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Library;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The folders of a server, as the page shows them.
/// </summary>
/// <param name="Id">The server.</param>
/// <param name="Name">The name of the server.</param>
/// <param name="Folders">Each folder to add to a library of its type, with its link.</param>
/// <param name="Movies">The link of the film folders together.</param>
/// <param name="Series">The link of the series folders together.</param>
/// <param name="SeriesGroupingOff">The libraries where a split series would not merge.</param>
/// <param name="SeriesLibraries">Every library holding a series folder, apart from the single library.</param>
/// <param name="Unregistered">
/// With the automatic libraries on, the libraries only on the folders of the server that the plugin does not keep, which
/// show its titles twice (row 133).
/// </param>
public sealed record ServerFolderStatus(
    Guid Id,
    string Name,
    IReadOnlyList<TypeFolderLink> Folders,
    FederatedPathStatus Movies,
    FederatedPathStatus Series,
    IReadOnlyList<string> SeriesGroupingOff,
    IReadOnlyList<string> SeriesLibraries,
    IReadOnlyList<string> Unregistered)
{
    /// <summary>Gets the servers whose series stay split from those of this server (Q46, row 77).</summary>
    public IReadOnlyList<Guid> SeriesSplitWith { get; init; } = [];
}
