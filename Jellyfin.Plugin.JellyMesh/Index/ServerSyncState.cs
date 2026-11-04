namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// How far the entries of a server can be trusted.
/// </summary>
public enum ServerSyncState
{
    /// <summary>The entries are the result of the last sync, which read the whole catalog.</summary>
    Complete,

    /// <summary>
    /// The last sync did not finish, or the entries could not be vouched for. They were kept as they were and say
    /// nothing about what is gone from the server.
    /// </summary>
    Stale
}
