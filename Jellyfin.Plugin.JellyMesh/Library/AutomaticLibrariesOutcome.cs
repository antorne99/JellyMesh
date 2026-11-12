namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The answer to a request to switch the libraries of a server on or off.
/// </summary>
public enum AutomaticLibrariesOutcome
{
    /// <summary>The work started in the background.</summary>
    Started,

    /// <summary>No server has that id.</summary>
    UnknownServer,

    /// <summary>The federated folder is not set.</summary>
    NoFederatedFolder,

    /// <summary>Libraries would be deleted, so the admin must confirm.</summary>
    ConfirmationRequired,

    /// <summary>A creation or a deletion is already running for the server.</summary>
    Busy
}
