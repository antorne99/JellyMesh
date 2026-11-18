using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The rename an edit makes, with the libraries it renames, for the warning of row 68.
/// </summary>
/// <param name="OldName">The saved name.</param>
/// <param name="NewName">The new name, trimmed.</param>
/// <param name="Libraries">The libraries of the plugin for this server that take the new name.</param>
public sealed record ServerRenameConsequence(string OldName, string NewName, IReadOnlyList<string> Libraries);
