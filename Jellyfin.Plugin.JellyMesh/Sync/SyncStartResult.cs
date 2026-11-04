namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// The outcome of asking for a sync.
/// </summary>
public enum SyncStartResult
{
    /// <summary>The sync started.</summary>
    Started,

    /// <summary>A sync is already running.</summary>
    AlreadyRunning,

    /// <summary>The secret key is not usable, so remote work is suspended.</summary>
    Suspended
}
