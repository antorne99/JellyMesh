using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Tells whether the folders of each server are linked to the libraries. The plugin never links them itself.
/// </summary>
public interface IFederatedFolderLinkChecker
{
    /// <summary>
    /// Checks the link state of the folders of the given servers.
    /// </summary>
    /// <param name="federatedFolderPath">The configured folder, not empty.</param>
    /// <param name="serverIds">The servers whose folders to check.</param>
    /// <returns>The links.</returns>
    FederatedFolderLinkReport Check(string federatedFolderPath, IReadOnlyList<Guid> serverIds);
}
