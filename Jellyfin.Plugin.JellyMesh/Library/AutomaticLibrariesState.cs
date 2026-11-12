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
    AutomaticLibrariesError Error);
