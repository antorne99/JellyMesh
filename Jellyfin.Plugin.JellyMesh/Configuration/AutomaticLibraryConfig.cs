namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// A library the plugin created for a server, as persisted in the plugin XML.
/// </summary>
public class AutomaticLibraryConfig
{
    /// <summary>
    /// Gets or sets the kind of the library.
    /// </summary>
    public ServerLibraryKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the library name as last seen.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library id as last seen, empty while not known.
    /// </summary>
    public string ItemId { get; set; } = string.Empty;
}
