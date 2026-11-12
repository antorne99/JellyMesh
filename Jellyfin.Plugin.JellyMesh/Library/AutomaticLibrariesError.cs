namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What went wrong in the last creation or deletion of the libraries of a server.
/// </summary>
public enum AutomaticLibrariesError
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>The name and the name with the GUID digits are both taken.</summary>
    NameTaken,

    /// <summary>The server refused to create a library.</summary>
    CreateFailed,

    /// <summary>The server refused to delete a library.</summary>
    RemoveFailed,

    /// <summary>The list of libraries or the federated folder could not be read.</summary>
    ReadFailed,

    /// <summary>Something else failed, such as saving the configuration.</summary>
    Failed
}
