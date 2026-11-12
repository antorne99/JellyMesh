namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What a run of the watch state carry did with one move.
/// </summary>
public enum WatchStateMoveStatus
{
    /// <summary>Done for every user.</summary>
    Carried,

    /// <summary>Done for some users; the others are listed with the reason, and a later run does them.</summary>
    UsersLeft,

    /// <summary>The old item or a new one is not in the library yet. A later run does it once they are.</summary>
    NotFound,

    /// <summary>The versions are not linked as one title yet, as the registry groups them. A later run does it once they are.</summary>
    NotLinked,

    /// <summary>The move is not valid: an old item that is not its stub, or a new item that is not a stub or the local primary.</summary>
    Refused,

    /// <summary>The move failed, or what it did could not be recorded. A later run does it again.</summary>
    Failed
}
