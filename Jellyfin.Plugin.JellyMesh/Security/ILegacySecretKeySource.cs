namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// Supplies the key of versions before 0.4, read only to move saved credentials to the key file (decision 42).
/// </summary>
public interface ILegacySecretKeySource
{
    /// <summary>
    /// Gets the old key string, as configured.
    /// </summary>
    /// <returns>The value, or null when it is not set.</returns>
    string? GetSecretKey();
}
