namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// A folder of a server the automatic migration added to a library of the admin, until the server or library is placed (§10 row 496).
/// </summary>
public class PlacedFolderConfig
{
    /// <summary>Gets or sets the id of the library, or its name when it has none.</summary>
    public string Library { get; set; } = string.Empty;

    /// <summary>Gets or sets the folder, relative to the federated folder.</summary>
    public string Folder { get; set; } = string.Empty;
}
