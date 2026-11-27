using System.Collections.ObjectModel;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The shared libraries, one per type with the folders of every server (§10 row 442), and the option that keeps them.
/// </summary>
public class SharedLibrariesConfig
{
    /// <summary>
    /// Gets or sets a value indicating whether the option is on. Off by default.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets the libraries the option created or adopted, followed by id.
    /// </summary>
    public Collection<AutomaticLibraryConfig> Libraries { get; } = [];

    /// <summary>
    /// Gets the keys of the libraries made since the option was turned on, so that one the admin deletes is not made again.
    /// </summary>
    public Collection<string> CreatedLibraryKeys { get; } = [];

    /// <summary>
    /// Gets the libraries asked of the server and not recorded yet (§10 row 403).
    /// </summary>
    public Collection<PendingLibraryConfig> PendingLibraries { get; } = [];

    /// <summary>
    /// Gets the ids of the libraries there when the option was last turned on, which are never deleted.
    /// </summary>
    public Collection<string> PreexistingLibraryIds { get; } = [];

    /// <summary>
    /// Gets the ids of the libraries there at the last check, which are never taken for a renamed one.
    /// </summary>
    public Collection<string> FollowedLibraryIds { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether a check was skipped or failed since <see cref="FollowedLibraryIds"/> was
    /// recorded: the next one takes no library for a renamed one (decision 173).
    /// </summary>
    public bool FollowSkipped { get; set; }

    /// <summary>
    /// Gets the names of the libraries left to the admin because they changed.
    /// </summary>
    public Collection<string> ReleasedLibraries { get; } = [];

    /// <summary>
    /// Tells whether a shared library lacks its blocks or is not recorded yet, which holds back every library scan (D42).
    /// </summary>
    /// <returns>Whether no library scan may run.</returns>
    public bool HoldsScans() => Libraries.Any(library => library.Unblocked) || PendingLibraries.Count > 0;
}
