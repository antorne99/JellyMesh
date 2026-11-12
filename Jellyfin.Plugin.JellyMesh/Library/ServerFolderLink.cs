using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The links of the two folders of one server.
/// </summary>
/// <param name="ServerId">The server.</param>
/// <param name="MoviesPath">The film folder of the server.</param>
/// <param name="SeriesPath">The series folder of the server.</param>
/// <param name="Movies">The link of the film folder.</param>
/// <param name="Series">The link of the series folder.</param>
/// <param name="SeriesGroupingOff">The series libraries holding the series folder with automatic series grouping off.</param>
/// <param name="SeriesLibraries">Every library holding the series folder, whose set decides which series merge (Q46).</param>
/// <param name="OnlyOnItsFolders">The libraries whose only paths are folders of this server.</param>
public sealed record ServerFolderLink(
    Guid ServerId,
    string MoviesPath,
    string SeriesPath,
    FederatedPathStatus Movies,
    FederatedPathStatus Series,
    IReadOnlyList<string> SeriesGroupingOff,
    IReadOnlyList<string> SeriesLibraries,
    IReadOnlyList<string> OnlyOnItsFolders);
