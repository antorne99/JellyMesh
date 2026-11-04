using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// Tells which local users are other JellyMesh servers. The check is made on the provider at every request, so nothing
/// depends on what the consumer claims.
/// </summary>
public sealed class PeerDirectory
{
    private readonly IPluginConfigurationStore _store;
    private readonly IAuthorizationContext _authorization;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerDirectory"/> class.
    /// </summary>
    /// <param name="store">The configuration store.</param>
    /// <param name="authorization">The authorization context.</param>
    public PeerDirectory(IPluginConfigurationStore store, IAuthorizationContext authorization)
    {
        _store = store;
        _authorization = authorization;
    }

    /// <summary>
    /// Finds what a user may do as a peer.
    /// </summary>
    /// <param name="userId">The local user.</param>
    /// <returns>The grant, null if the user is not a peer.</returns>
    public PeerGrant? Find(Guid userId)
    {
        return _store.Read(config => config.Peers
            .Where(peer => peer.UserId == userId)
            .Select(peer => new PeerGrant(peer.UserId, peer.SharedLibraryIds.ToHashSet(StringComparer.OrdinalIgnoreCase), peer.MaxStreams))
            .FirstOrDefault());
    }

    /// <summary>
    /// Finds the peer behind a request. An anonymous request has none, so a request without a token is out of reach here.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The peer, null for anyone else.</returns>
    public async Task<PeerCaller?> FindCallerAsync(HttpContext context)
    {
        if (!_store.Read(config => config.Peers.Count > 0))
        {
            return null;
        }

        var authorization = await _authorization.GetAuthorizationInfo(context).ConfigureAwait(false);
        return authorization.User is { } user && Find(user.Id) is { } grant ? new PeerCaller(grant, user) : null;
    }
}
