namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// An imported remote library that two consecutive correct answers of the remote no longer listed (§10 row 436).
/// </summary>
public class VanishedLibraryConfig
{
    /// <summary>Gets or sets the id the library had on the remote.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets its name as last read, for the warning on the page.</summary>
    public string Name { get; set; } = string.Empty;
}
