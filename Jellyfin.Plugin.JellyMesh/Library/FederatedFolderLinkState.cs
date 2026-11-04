namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Whether the federated folder is linked to the libraries.
/// </summary>
public enum FederatedFolderLinkState
{
    /// <summary>
    /// The link was not checked.
    /// </summary>
    NotChecked,

    /// <summary>
    /// The Film and Serie subfolders are paths of libraries of the right type.
    /// </summary>
    Linked,

    /// <summary>
    /// At least one subfolder is not a path of a library of the right type. The configuration page warns.
    /// </summary>
    NotLinked
}
