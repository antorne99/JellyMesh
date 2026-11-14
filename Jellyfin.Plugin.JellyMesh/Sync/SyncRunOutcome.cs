namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// How a sync ended.
/// </summary>
public enum SyncRunOutcome
{
    /// <summary>Every server was read and everything was saved and written.</summary>
    Succeeded,

    /// <summary>Some server could not be read, or something could not be saved or written.</summary>
    PartlyFailed,

    /// <summary>No server could be read, or the federated folder could not be brought in step.</summary>
    Failed,

    /// <summary>The sync was stopped, as when the server shuts down.</summary>
    Cancelled
}
