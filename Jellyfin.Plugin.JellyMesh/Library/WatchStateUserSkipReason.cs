namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Why the watch state of a move was not carried for a user.
/// </summary>
public enum WatchStateUserSkipReason
{
    /// <summary>The user sees none of the new items (decision 149).</summary>
    NoVisibleVersion,

    /// <summary>Jellyfin has no row for the user on an item.</summary>
    NoUserData,

    /// <summary>A row could not be saved.</summary>
    NotSaved,

    /// <summary>The rows were saved, but the record of the carry was not (decision 155).</summary>
    NotRecorded
}
