namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// What the plugin database is after it was opened.
/// </summary>
public enum StoreState
{
    /// <summary>The database is open and current.</summary>
    Healthy,

    /// <summary>The file has a schema newer than this build knows, so it is left untouched.</summary>
    NewerSchema,

    /// <summary>The file fails the integrity check or SQLite reads it as corrupt, and it could not be set aside.</summary>
    Damaged,

    /// <summary>The file cannot be opened or kept private, for example for permissions or a full disk.</summary>
    Unavailable
}
