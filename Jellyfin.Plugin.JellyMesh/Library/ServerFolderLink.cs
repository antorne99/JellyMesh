using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The links of the folders of one server: its type folders, or its two 0.5 folders while titles are in that layout.
/// </summary>
/// <param name="ServerId">The server.</param>
/// <param name="Folders">Each folder of the server with its link.</param>
/// <param name="Movies">The link of its film folders together: linked only when each is.</param>
/// <param name="Series">The link of its series folders together: linked only when each is.</param>
/// <param name="SeriesGroupingOff">The series libraries holding a series folder with automatic series grouping off.</param>
/// <param name="SeriesLibraries">Every library holding a series folder apart from the single library, whose set decides which series merge (Q46).</param>
/// <param name="OnlyOnItsFolders">The libraries whose only paths are folders of this server.</param>
public sealed record ServerFolderLink(
    Guid ServerId,
    IReadOnlyList<TypeFolderLink> Folders,
    FederatedPathStatus Movies,
    FederatedPathStatus Series,
    IReadOnlyList<string> SeriesGroupingOff,
    IReadOnlyList<string> SeriesLibraries,
    IReadOnlyList<string> OnlyOnItsFolders)
{
    /// <summary>Gets the servers whose series folders share a library with a series folder of this one but sit in other libraries.</summary>
    public IReadOnlyList<Guid> SeriesSplitWith { get; init; } = [];
}
