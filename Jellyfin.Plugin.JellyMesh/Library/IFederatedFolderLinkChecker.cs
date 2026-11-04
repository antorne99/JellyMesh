namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Tells whether the federated folder is linked to the libraries. The plugin never links it itself.
/// </summary>
public interface IFederatedFolderLinkChecker
{
    /// <summary>
    /// Checks the link state of the federated folder.
    /// </summary>
    /// <param name="federatedFolderPath">The configured folder, not empty.</param>
    /// <returns>The links.</returns>
    FederatedFolderLinkReport Check(string federatedFolderPath);
}
