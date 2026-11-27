using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Configuration;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What the page shows about the libraries the plugin keeps for a server.
/// </summary>
/// <param name="Enabled">Whether the switch is on.</param>
/// <param name="Busy">Whether a creation or a deletion is running.</param>
/// <param name="Libraries">The libraries in the registry.</param>
/// <param name="Released">The libraries left to the admin.</param>
/// <param name="Error">What went wrong in the last run.</param>
public sealed record AutomaticLibrariesState(
    bool Enabled,
    bool Busy,
    IReadOnlyList<AutomaticLibraryStatus> Libraries,
    IReadOnlyList<string> Released,
    AutomaticLibrariesError Error)
{
    /// <summary>Gets the library mode of the server.</summary>
    public LibraryMode Mode { get; init; }

    /// <summary>Gets the folders of the server to add to libraries by hand in "none" mode, each with the type it needs.</summary>
    public IReadOnlyList<ManualLibraryPath> ManualPaths { get; init; } = [];

    /// <summary>Gets the remote libraries the server no longer has, by name: they left the imported ones (§10 row 436).</summary>
    public IReadOnlyList<string> Vanished { get; init; } = [];

    /// <summary>Gets the libraries of the admin that kept the last check from making or adopting a library, and why.</summary>
    public IReadOnlyList<LibraryConflict> Conflicts { get; init; } = [];

    /// <summary>Gets the users who lost titles in the last change of library mode, by library of the new mode.</summary>
    public IReadOnlyList<AccessLoss> LostAccess { get; init; } = [];

    /// <summary>Gets a value indicating whether a library the mode wants is not in the registry, as after the admin deleted it.</summary>
    public bool Missing { get; init; }
}
