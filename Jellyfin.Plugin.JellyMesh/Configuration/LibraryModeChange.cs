using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The change of library mode an edit makes, with its effects for the confirmation (section 3.8).
/// </summary>
/// <param name="From">The saved mode.</param>
/// <param name="To">The new mode.</param>
/// <param name="Deleted">The libraries of the plugin for this server that the change deletes.</param>
/// <param name="Released">The adopted libraries that the change gives back to the administrator.</param>
public sealed record LibraryModeChange(LibraryMode From, LibraryMode To, IReadOnlyList<string> Deleted, IReadOnlyList<string> Released)
{
    /// <summary>Gets the users who would lose titles, by library of the new mode: they had only some of the old libraries.</summary>
    public IReadOnlyList<AccessLoss> LostAccess { get; init; } = [];
}
