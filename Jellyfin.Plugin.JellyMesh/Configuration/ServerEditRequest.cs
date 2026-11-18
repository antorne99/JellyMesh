using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The edit form of a server, for its check and its save (row 432). A class rather than a record, so that no generated
/// <see cref="object.ToString"/> can print the password.
/// </summary>
public sealed class ServerEditRequest
{
    /// <summary>
    /// Gets the display name.
    /// </summary>
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the base URL of the remote, as the administrator entered it.
    /// </summary>
    [Required]
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// Gets the dedicated user name.
    /// </summary>
    [Required]
    public string Username { get; init; } = string.Empty;

    /// <summary>
    /// Gets the dedicated user password. Empty keeps the saved one; it is never echoed back.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Gets the remote libraries to import. Null keeps the saved ones.
    /// </summary>
    public IReadOnlyList<string>? LibraryIds { get; init; }

    /// <summary>
    /// Builds the edit the registry checks or saves.
    /// </summary>
    /// <returns>The edit.</returns>
    internal RemoteServerEdit ToEdit() => new()
    {
        Name = Name,
        Url = Url,
        Username = Username,
        Password = Password,
        LibraryIds = LibraryIds
    };
}
