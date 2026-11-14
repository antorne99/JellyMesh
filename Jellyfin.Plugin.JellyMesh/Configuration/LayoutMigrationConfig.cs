using System;
using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyMesh.Library;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The migration from the flat layout as persisted in the plugin XML (Q55-Q57). A mutable class because XmlSerializer requires it.
/// </summary>
public class LayoutMigrationConfig
{
    /// <summary>Gets or sets the mode the admin chose in the banner.</summary>
    public LayoutMigrationMode Mode { get; set; }

    /// <summary>Gets or sets the step the automatic migration is at.</summary>
    public AutomaticMigrationStep Step { get; set; }

    /// <summary>Gets or sets why the automatic migration stopped, <see cref="AutomaticMigrationError.None"/> while it goes on.</summary>
    public AutomaticMigrationError Error { get; set; }

    /// <summary>Gets or sets the library where it stopped, empty when the error names none.</summary>
    public string ErrorLibrary { get; set; } = string.Empty;

    /// <summary>
    /// Gets the servers whose folders need no placement in automatic mode: in the libraries by the migration, there when
    /// the settings chose automatic, or placed since (Q58, D24).
    /// </summary>
    public Collection<Guid> PlacedServers { get; } = [];
}
