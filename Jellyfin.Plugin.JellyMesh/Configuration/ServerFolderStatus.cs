using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Library;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The two folders of a server, as the page shows them.
/// </summary>
/// <param name="Id">The server.</param>
/// <param name="Name">The name of the server.</param>
/// <param name="MoviesPath">The path for a film library.</param>
/// <param name="SeriesPath">The path for a series library.</param>
/// <param name="Movies">The link of the film path.</param>
/// <param name="Series">The link of the series path.</param>
/// <param name="SeriesGroupingOff">The libraries where a split series would not merge.</param>
/// <param name="SeriesLibraries">Every library holding the series path.</param>
/// <param name="Unregistered">
/// With the automatic libraries on, the libraries only on the folders of the server that the plugin does not keep, which
/// show its titles twice (row 133).
/// </param>
public sealed record ServerFolderStatus(
    Guid Id,
    string Name,
    string MoviesPath,
    string SeriesPath,
    FederatedPathStatus Movies,
    FederatedPathStatus Series,
    IReadOnlyList<string> SeriesGroupingOff,
    IReadOnlyList<string> SeriesLibraries,
    IReadOnlyList<string> Unregistered);
