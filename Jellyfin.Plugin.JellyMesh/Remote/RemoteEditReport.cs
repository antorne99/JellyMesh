using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// What a check or a save of the edit form found on the remote; on a refusal, the field it is about. Holds nothing secret.
/// </summary>
/// <param name="Field">The field a refusal is about, <see cref="RemoteEditField.None"/> on success.</param>
public sealed record RemoteEditReport(RemoteEditField Field)
{
    /// <summary>
    /// Gets the fields the edit changes.
    /// </summary>
    public RemoteEditChanges Changes { get; init; }

    /// <summary>
    /// Gets the name the remote gives itself, cleaned and bounded. Untrusted text, null when not read.
    /// </summary>
    public string? ServerName { get; init; }

    /// <summary>
    /// Gets the libraries the dedicated user sees, null when the remote was not read.
    /// </summary>
    public IReadOnlyList<RemoteLibrary>? Libraries { get; init; }

    /// <summary>
    /// Gets the libraries imported with the edit: those chosen that the user sees, or the saved ones without a read.
    /// </summary>
    public IReadOnlyList<string> LibraryIds { get; init; } = [];
}
