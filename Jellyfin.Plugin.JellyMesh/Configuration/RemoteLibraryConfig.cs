namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// An imported remote library as the plugin last read it, so that its libraries and folders are known with the remote offline.
/// </summary>
public class RemoteLibraryConfig
{
    /// <summary>Gets or sets the id of the library on the remote.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the library on the remote.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the collection type of the library on the remote, such as <c>movies</c>.</summary>
    public string CollectionType { get; set; } = string.Empty;
}
