namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The answer to a request to rename a server.
/// </summary>
public enum ServerRenameOutcome
{
    /// <summary>The rename started in the background.</summary>
    Started,

    /// <summary>The name is blank.</summary>
    InvalidName,

    /// <summary>No server has that id.</summary>
    UnknownServer,

    /// <summary>The admin must confirm, after the warning of Q38.</summary>
    ConfirmationRequired,

    /// <summary>A rename of the server is running, or its stubs still wait for a scan.</summary>
    Busy
}
