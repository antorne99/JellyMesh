namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// A library the plugin created or adopted for a server, as kept in the plugin database.
/// </summary>
public class AutomaticLibraryConfig
{
    /// <summary>
    /// Gets or sets the kind of the library.
    /// </summary>
    public ServerLibraryKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the id of the remote library the library holds alone, empty for a library of a type.
    /// </summary>
    public string RemoteLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library name as last seen.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library id as last seen, empty while not known.
    /// </summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the library was the admin's, adopted for exactly its paths and type: the
    /// plugin keeps its paths but never renames or deletes it, and on release removes only its own paths (§10 rows 440-441).
    /// </summary>
    public bool Adopted { get; set; }

    /// <summary>
    /// Gets what tells the library apart among those of its server: its remote library, or its kind.
    /// </summary>
    /// <returns>The key, as kept in <see cref="RemoteServerConfig.CreatedLibraryKeys"/>.</returns>
    public string KeyOf() => RemoteLibraryId.Length > 0 ? RemoteLibraryId : Kind.ToString();
}
