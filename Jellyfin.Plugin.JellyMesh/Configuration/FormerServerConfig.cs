using System;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// A server removed from the page, as persisted in the plugin XML. A mutable class because XmlSerializer requires it.
/// </summary>
public class FormerServerConfig
{
    /// <summary>
    /// Gets or sets the local identifier the server had, which names its folder and keeps its label.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the Server Id the remote gave itself, as 32 lowercase digits.
    /// </summary>
    public string JellyfinServerId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name the server had.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}
