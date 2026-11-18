using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Library;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// What saving an edit does beyond its fields (row 432). Each one is absent, null or empty or zero, when it does not apply.
/// </summary>
/// <param name="Rename">The rename of the server, null when the name stays.</param>
/// <param name="RemovedLibraries">The imported libraries the save takes out.</param>
/// <param name="StoppedStreams">How many playback sessions of the server stop, when the dedicated user changes.</param>
public sealed record ServerEditConsequences(ServerRenameConsequence? Rename, IReadOnlyList<ImportedLibraryRemoval> RemovedLibraries, int StoppedStreams)
{
    /// <summary>
    /// Gets the change of the library mode, null when the mode stays.
    /// </summary>
    public LibraryModeChange? ModeChange { get; init; }

    /// <summary>
    /// Gets a value indicating whether the save has any consequence to confirm.
    /// </summary>
    public bool Any => Rename is not null || RemovedLibraries.Count > 0 || StoppedStreams > 0 || ModeChange is not null;
}
