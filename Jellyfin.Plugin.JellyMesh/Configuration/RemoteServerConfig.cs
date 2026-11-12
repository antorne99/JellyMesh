using System;
using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// A remote server as persisted in the plugin XML. A mutable class because XmlSerializer requires it.
/// </summary>
public class RemoteServerConfig
{
    /// <summary>
    /// Gets or sets the local identifier of this server.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the Server Id the remote gives itself at login, as 32 lowercase digits, empty while not known.
    /// </summary>
    public string JellyfinServerId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the normalized tailnet base URL.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the credentials as encrypted by <see cref="Security.ICredentialVault"/>.
    /// </summary>
    public string EncryptedCredentials { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the mode found by the last capability detection.
    /// </summary>
    public RemoteMode Mode { get; set; } = RemoteMode.Unknown;

    /// <summary>
    /// Gets or sets the JellyMesh protocol version of the remote, 0 when it has none.
    /// </summary>
    public int ProtocolVersion { get; set; }

    /// <summary>
    /// Gets the identifiers of the remote libraries to import.
    /// </summary>
    public Collection<string> ImportedLibraryIds { get; } = [];
}
