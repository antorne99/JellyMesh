using System;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The libraries of a removed server still to delete, as persisted in the plugin XML (decision 140). A mutable class
/// because XmlSerializer requires it.
/// </summary>
public class RemovedServerLibrariesConfig
{
    /// <summary>
    /// Gets or sets the local identifier the server had, which names its folder.
    /// </summary>
    public Guid ServerId { get; set; }

    /// <summary>
    /// Gets the libraries of its registry.
    /// </summary>
    public Collection<AutomaticLibraryConfig> Libraries { get; } = [];

    /// <summary>
    /// Gets the ids of the libraries there when its switch was last turned on, which are never deleted.
    /// </summary>
    public Collection<string> PreexistingLibraryIds { get; } = [];

    /// <summary>
    /// Gets the ids of the libraries there at the last check of its registry, which are never taken for a renamed one.
    /// </summary>
    public Collection<string> FollowedLibraryIds { get; } = [];
}
