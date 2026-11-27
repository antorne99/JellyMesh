using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The shared libraries a switch-off named and the admin confirmed (D48).
/// </summary>
/// <param name="ToDelete">The libraries the plugin made, to delete.</param>
/// <param name="ToRelease">The adopted libraries, to release.</param>
public sealed record SharedLibrariesConfirmation(IReadOnlyCollection<string> ToDelete, IReadOnlyCollection<string> ToRelease);
