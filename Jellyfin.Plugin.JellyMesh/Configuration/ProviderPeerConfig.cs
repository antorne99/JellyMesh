using System;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// A connected JellyMesh server as persisted in the plugin XML (decision 12). A mutable class because XmlSerializer
/// requires it.
/// </summary>
public class ProviderPeerConfig
{
    /// <summary>
    /// Gets or sets the dedicated local user the other server logs in with.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the most streams at the same time.
    /// </summary>
    public int MaxStreams { get; set; }

    /// <summary>
    /// Gets the libraries shared with that server.
    /// </summary>
    public Collection<string> SharedLibraryIds { get; } = [];
}
