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
    Busy,

    /// <summary>The pending stub moves of the server were dropped.</summary>
    Cancelled,

    /// <summary>An old file of a pending stub move could not be deleted, so that move stays.</summary>
    CancelFailed
}
