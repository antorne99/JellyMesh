using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Configuration;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What a change of library mode does to the libraries of a server, for the confirmation of the edit (row 438).
/// </summary>
/// <param name="From">The saved mode.</param>
/// <param name="To">The new mode.</param>
/// <param name="DeletedLibraries">The libraries the plugin made that the change deletes, once those of the new mode are made.</param>
/// <param name="ReleasedLibraries">The adopted libraries that lose the folders of the server and stay.</param>
public sealed record LibraryModeChange(LibraryMode From, LibraryMode To, IReadOnlyList<string> DeletedLibraries, IReadOnlyList<string> ReleasedLibraries);
