using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Configuration;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A plugin library a library mode wants.
/// </summary>
/// <param name="Key">What tells it apart among the libraries of its server: the kind, or the remote library.</param>
/// <param name="Kind">The kind, which gives its local type.</param>
/// <param name="RemoteLibraryId">The remote library it holds alone, empty for a library of a type.</param>
/// <param name="Suffix">What its name has after "{Server} - ".</param>
/// <param name="Paths">Its paths.</param>
internal sealed record PlannedLibrary(string Key, ServerLibraryKind Kind, string RemoteLibraryId, string Suffix, IReadOnlyList<string> Paths);
