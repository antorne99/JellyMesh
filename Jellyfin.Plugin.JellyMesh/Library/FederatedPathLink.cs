namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// How a subfolder of the federated folder is linked.
/// </summary>
public enum FederatedPathLink
{
    /// <summary>The link was not checked.</summary>
    NotChecked,

    /// <summary>No library has this path.</summary>
    NotLinked,

    /// <summary>A library of the right type or a mixed one has this path, and none of another type has it.</summary>
    Linked,

    /// <summary>A library has this path with other capitals, which on this server is another folder.</summary>
    DifferentCase,

    /// <summary>A library of another type has this path, whether or not one of the right type has it too.</summary>
    WrongLibraryType
}
