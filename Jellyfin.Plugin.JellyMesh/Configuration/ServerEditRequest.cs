using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The edit form of a server, for its check and its save (row 432). A class rather than a record, so that no generated
/// <see cref="object.ToString"/> can print the password. Fields are optional here so that an empty one is refused on its
/// field by the registry, not by model validation.
/// </summary>
public sealed class ServerEditRequest
{
    /// <summary>
    /// Gets the display name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the base URL of the remote, as the administrator entered it.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Gets the dedicated user name. Empty keeps the saved one, which the page never gets back.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Gets the dedicated user password. Empty keeps the saved one; it is never echoed back.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Gets the remote libraries to import. Null keeps the saved ones.
    /// </summary>
    public IReadOnlyList<string>? LibraryIds { get; init; }

    /// <summary>
    /// Gets the library mode of the server (section 3.8). Null keeps the saved one.
    /// </summary>
    public LibraryMode? LibraryMode { get; init; }

    /// <summary>
    /// Builds the edit the registry checks or saves.
    /// </summary>
    /// <returns>The edit.</returns>
    internal RemoteServerEdit ToEdit() => new()
    {
        Name = Name ?? string.Empty,
        Url = Url ?? string.Empty,
        Username = Username ?? string.Empty,
        Password = Password,
        LibraryIds = LibraryIds
    };
}
