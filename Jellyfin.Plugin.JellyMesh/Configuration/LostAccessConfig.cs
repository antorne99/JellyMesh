namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// A user who lost titles of a library in the last change of library mode, for the warning on the page (§10 row 438).
/// </summary>
public class LostAccessConfig
{
    /// <summary>Gets or sets the name of the library of the new mode.</summary>
    public string Library { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the user. Untrusted text.</summary>
    public string User { get; set; } = string.Empty;
}
