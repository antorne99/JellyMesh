using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What to do with a library the plugin wants: adopt one of the admin, wait, make it when nothing is in the way, or nothing.
/// </summary>
/// <param name="Adopt">The library of the admin to adopt.</param>
/// <param name="Wait">Whether the library to adopt has no id yet.</param>
/// <param name="Conflicts">The libraries of the admin in the way.</param>
internal sealed record AdoptionOutcome(VirtualFolderInfo? Adopt, bool Wait, IReadOnlyList<LibraryConflict> Conflicts)
{
    /// <summary>Gets a value indicating whether the plugin makes the library.</summary>
    public bool Create => Adopt is null && !Wait && Conflicts.Count == 0;
}
