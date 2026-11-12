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

    /// <summary>
    /// Gets or sets a value indicating whether the plugin keeps a Film and a Serie library for this server.
    /// </summary>
    public bool AutomaticLibraries { get; set; }

    /// <summary>
    /// Gets the libraries the plugin created for this server and still manages.
    /// </summary>
    public Collection<AutomaticLibraryConfig> AutomaticLibraryRegistry { get; } = [];

    /// <summary>
    /// Gets the ids of the libraries there when the switch was last turned on, outside the registry: they are the admin's,
    /// so the plugin never takes them for its own (decision 131).
    /// </summary>
    public Collection<string> PreexistingLibraryIds { get; } = [];

    /// <summary>
    /// Gets the ids of the libraries there at the last check of the registry: none of them is ever taken for a renamed one
    /// (decision 157).
    /// </summary>
    public Collection<string> FollowedLibraryIds { get; } = [];

    /// <summary>
    /// Gets the names of the libraries left to the admin because their path or type changed, or they are gone.
    /// </summary>
    public Collection<string> ReleasedAutomaticLibraries { get; } = [];
}
