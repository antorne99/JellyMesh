namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// What a sync read.
/// </summary>
public enum SyncRunKind
{
    /// <summary>What changed since the last read of each server.</summary>
    Incremental,

    /// <summary>Every chosen library whole: the nightly one, or one started from the page.</summary>
    Reconciliation
}
