namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Supplies the federated folder set outside the configuration page.
/// </summary>
public interface IFederatedRootSource
{
    /// <summary>
    /// Gets the value as set, not yet checked.
    /// </summary>
    /// <returns>The value, or <c>null</c> if it is not set.</returns>
    string? GetFederatedRoot();
}
