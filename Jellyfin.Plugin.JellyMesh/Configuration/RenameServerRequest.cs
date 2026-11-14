namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Renames a server.
/// </summary>
public sealed record RenameServerRequest
{
    /// <summary>
    /// Gets the new name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets a value indicating whether the admin confirmed after the warning.
    /// </summary>
    public bool Confirmed { get; init; }
}
