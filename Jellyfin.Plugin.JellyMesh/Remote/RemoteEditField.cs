namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// The field of the edit form a refusal is about, so the page shows it there (row 432).
/// </summary>
public enum RemoteEditField
{
    /// <summary>No single field: the form as a whole.</summary>
    None,

    /// <summary>The display name.</summary>
    Name,

    /// <summary>The base URL, and the server that answers there.</summary>
    Url,

    /// <summary>The dedicated user.</summary>
    Username,

    /// <summary>The password of the dedicated user.</summary>
    Password,

    /// <summary>The libraries to import.</summary>
    LibraryIds
}
