using System;
using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// A remote server as kept in the servers table of the plugin database.
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
    /// Gets or sets the normalized base URL of the remote.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user and password as encrypted by <see cref="Security.ICredentialVault"/>, bound to <see cref="Id"/>,
    /// with the key file of the data folder, which never enters the database.
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
    /// Gets the imported remote libraries as last read, with their names and types.
    /// </summary>
    public Collection<RemoteLibraryConfig> RemoteLibraries { get; } = [];

    /// <summary>
    /// Gets or sets which local libraries hold the type folders of this server. None for a server kept from before the
    /// modes; one added later takes <see cref="PluginConfiguration.NewServerLibraryMode"/>.
    /// </summary>
    public LibraryMode LibraryMode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin keeps libraries for this server: setting it on keeps a mode
    /// already chosen, or chooses one per type; setting it off chooses none.
    /// </summary>
    public bool AutomaticLibraries
    {
        get => LibraryMode != LibraryMode.None;
        set => LibraryMode = !value ? LibraryMode.None : LibraryMode == LibraryMode.None ? LibraryMode.ByType : LibraryMode;
    }

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
    /// Gets or sets a value indicating whether a check of the registry was skipped or failed since
    /// <see cref="FollowedLibraryIds"/> was recorded: the next one takes no library for a renamed one (decision 173).
    /// </summary>
    public bool FollowSkipped { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a rename of this server started and has not finished, so the page shows it
    /// after a restart too (§10 row 289).
    /// </summary>
    public bool RenameUnfinished { get; set; }

    /// <summary>
    /// Gets the keys of the libraries the plugin created for this server since its libraries were last turned on: a library
    /// the admin deleted is not made again.
    /// </summary>
    public Collection<string> CreatedLibraryKeys { get; } = [];

    /// <summary>
    /// Gets the names of the libraries left to the admin because their path or type changed, or they are gone.
    /// </summary>
    public Collection<string> ReleasedAutomaticLibraries { get; } = [];
}
