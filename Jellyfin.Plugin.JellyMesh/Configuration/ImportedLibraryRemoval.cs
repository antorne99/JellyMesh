namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// An imported library an edit takes out: left out by the admin, or no longer visible to the user (row 434).
/// </summary>
/// <param name="Id">The remote identifier.</param>
/// <param name="Name">The remote name, or the one last read when the user no longer sees it; null when none was read. Untrusted text.</param>
/// <param name="NoLongerVisible">Whether the dedicated user no longer sees it, so it goes whatever is chosen.</param>
public sealed record ImportedLibraryRemoval(string Id, string? Name, bool NoLongerVisible);
