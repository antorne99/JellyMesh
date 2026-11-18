using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// A library the plugin is about to make, saved before it asks the server for it, so that one made before a crash is
/// recorded and no other library is taken for it.
/// </summary>
public class PendingLibraryConfig
{
    /// <summary>Gets or sets the kind of the library.</summary>
    public ServerLibraryKind Kind { get; set; }

    /// <summary>Gets or sets the name the library is made with.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the remote library it shows, empty for a library by type.</summary>
    public string RemoteLibraryId { get; set; } = string.Empty;

    /// <summary>Gets the folders the library is made with.</summary>
    public Collection<string> Paths { get; } = [];

    /// <summary>
    /// Gets the key of the library, as <see cref="AutomaticLibraryConfig.KeyOf"/> gives it.
    /// </summary>
    /// <returns>The key.</returns>
    public string KeyOf() => RemoteLibraryId.Length > 0 ? RemoteLibraryId : Kind.ToString();
}
