using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Everything the configuration page shows that is not a stock setting. Holds no credentials.
/// </summary>
/// <param name="KeyState">The state of the secret key.</param>
/// <param name="Suspended">Whether sync and remote playback are suspended.</param>
/// <param name="Warning">The warning to show, if any.</param>
/// <param name="FederatedFolder">The federated folder.</param>
/// <param name="Servers">The configured servers.</param>
public sealed record FederationStatus(
    KeyState KeyState,
    bool Suspended,
    string? Warning,
    FederatedFolderStatus FederatedFolder,
    IReadOnlyList<ServerStatus> Servers)
{
    /// <summary>
    /// Gets a value indicating whether <c>JELLYMESH_SECRET_KEY</c> is still set. It is no longer used, so it can be removed.
    /// </summary>
    public bool LegacyKeySet { get; init; }

    /// <summary>
    /// Gets the libraries of removed servers that the plugin left to the admin when the federated folder changed.
    /// </summary>
    public IReadOnlyList<string> ReleasedLibraries { get; init; } = [];
}
