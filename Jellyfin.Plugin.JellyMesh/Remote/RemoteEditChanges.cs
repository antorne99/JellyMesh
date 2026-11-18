using System;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// The fields an edit of a server changes.
/// </summary>
[Flags]
public enum RemoteEditChanges
{
    /// <summary>Nothing changes.</summary>
    None = 0,

    /// <summary>The display name.</summary>
    Name = 1,

    /// <summary>The base URL.</summary>
    Url = 2,

    /// <summary>The dedicated user.</summary>
    User = 4,

    /// <summary>The password of the dedicated user.</summary>
    Password = 8,

    /// <summary>The libraries to import, including those the user no longer sees.</summary>
    Libraries = 16
}
