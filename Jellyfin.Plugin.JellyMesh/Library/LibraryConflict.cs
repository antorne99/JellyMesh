using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Configuration;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A library of the admin on the paths of a library the plugin wants, which it neither makes nor adopts.
/// </summary>
/// <param name="Library">The name of the library of the admin.</param>
/// <param name="Reason">Why it is in the way.</param>
/// <param name="Kind">The kind of the library the plugin wants.</param>
/// <param name="RemoteLibraryId">The remote library it would hold alone, empty for a library of a type.</param>
/// <param name="Paths">The paths the plugin would give it, which a library of exactly these paths and type would take.</param>
public sealed record LibraryConflict(string Library, LibraryConflictReason Reason, ServerLibraryKind Kind, string RemoteLibraryId, IReadOnlyList<string> Paths);
