using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// Works out the decisions from the stored entries. They are never stored, because a new source can change identities.
/// </summary>
public interface IFederationResolver
{
    /// <summary>
    /// Resolves the whole catalog against the local library.
    /// </summary>
    /// <returns>The sources of each identity.</returns>
    FederationView Resolve();

    /// <summary>
    /// Resolves one snapshot of the catalog against the local library, with the given servers as the configured ones.
    /// </summary>
    /// <param name="snapshot">The index snapshot.</param>
    /// <param name="servers">The configured servers.</param>
    /// <returns>The sources of each identity.</returns>
    FederationView Resolve(IndexSnapshot snapshot, IReadOnlyList<RemoteServerInfo> servers);
}
