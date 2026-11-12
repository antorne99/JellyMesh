namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Turns the libraries of a server on or off.
/// </summary>
public sealed record SetAutomaticLibrariesRequest
{
    /// <summary>
    /// Gets a value indicating whether the plugin should keep a Film and a Serie library for the server.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets a value indicating whether the admin confirmed the deletion of the libraries.
    /// </summary>
    public bool Confirmed { get; init; }
}
