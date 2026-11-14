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

    /// <summary>
    /// Gets or sets a value indicating whether a check of the registry was skipped or failed since
    /// <see cref="FollowedLibraryIds"/> was recorded: the next one takes no library for a renamed one (decision 173).
    /// </summary>
    public bool FollowSkipped { get; set; }

    /// <summary>
    /// Gets the names of its libraries left to the admin because the federated folder changed (decision 158).
    /// </summary>
    public Collection<string> ReleasedLibraries { get; } = [];

    /// <summary>
    /// Gets the names of <see cref="ReleasedLibraries"/> the admin hid from the page. The libraries themselves stay.
    /// </summary>
    public Collection<string> HiddenReleasedLibraries { get; } = [];
}
