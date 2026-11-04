namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// What a remote server is capable of.
/// </summary>
public enum RemoteMode
{
    /// <summary>
    /// Not determined. Only a 404 on the capability endpoint proves a remote is standard.
    /// </summary>
    Unknown,

    /// <summary>
    /// Stock Jellyfin.
    /// </summary>
    Standard,

    /// <summary>
    /// The remote runs JellyMesh.
    /// </summary>
    JellyMesh
}
