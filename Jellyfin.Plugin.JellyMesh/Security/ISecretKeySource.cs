namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// Supplies the raw secret key string.
/// </summary>
public interface ISecretKeySource
{
    /// <summary>
    /// Gets the secret key string, as configured.
    /// </summary>
    /// <returns>The value, or null when it is not set.</returns>
    string? GetSecretKey();
}
