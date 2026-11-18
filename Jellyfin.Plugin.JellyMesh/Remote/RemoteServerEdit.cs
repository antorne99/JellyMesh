using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// The fields of the edit form of a configured server (row 432). A class rather than a record, so that no generated
/// <see cref="object.ToString"/> can print the password.
/// </summary>
public sealed class RemoteServerEdit
{
    /// <summary>
    /// Gets the display name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the base URL, as the administrator entered it.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Gets the dedicated user name.
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// Gets the dedicated user password. Null or empty keeps the saved one.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Gets the remote libraries to import. Null keeps the saved ones.
    /// </summary>
    public IReadOnlyList<string>? LibraryIds { get; init; }
}
