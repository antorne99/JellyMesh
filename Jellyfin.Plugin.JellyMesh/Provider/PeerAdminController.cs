using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// Backs the part of the configuration page about the servers connected to this one, for administrators only. Decision
/// 12: a connected server is a dedicated non-admin user, and what is shared attaches to that user.
/// </summary>
[ApiController]
[Route("JellyMesh/Admin/Peers")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class PeerAdminController : ControllerBase
{
    private readonly IPluginConfigurationStore _store;
    private readonly IUserManager _users;
    private readonly ProviderCatalog _catalog;
    private readonly StreamSlots _slots;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerAdminController"/> class.
    /// </summary>
    /// <param name="store">The configuration store.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="catalog">The catalog.</param>
    /// <param name="slots">The stream slots.</param>
    public PeerAdminController(IPluginConfigurationStore store, IUserManager users, ProviderCatalog catalog, StreamSlots slots)
    {
        _store = store;
        _users = users;
        _catalog = catalog;
        _slots = slots;
    }

    /// <summary>
    /// Lists the connected servers.
    /// </summary>
    /// <returns>The servers.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<PeerStatus>> GetPeers()
    {
        var peers = _store.Read(config => config.Peers.Select(peer => (peer.UserId, Libraries: peer.SharedLibraryIds.ToList(), peer.MaxStreams)).ToList());
        return peers
            .Select(peer => new PeerStatus(peer.UserId, _users.GetUserById(peer.UserId)?.Username ?? string.Empty, peer.Libraries, peer.MaxStreams, _slots.Count(peer.UserId)))
            .ToList();
    }

    /// <summary>
    /// Lists the users and libraries that can be chosen.
    /// </summary>
    /// <returns>The options.</returns>
    [HttpGet("Options")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PeerOptions> GetOptions()
    {
        var users = _users.GetUsers().Where(user => !IsAdministrator(user)).Select(user => new PeerUserOption(user.Id, user.Username)).ToList();
        return new PeerOptions(users, _catalog.ReadLibraries());
    }

    /// <summary>
    /// Connects a server, or changes what is shared with it. Taking a library away cuts what is playing from it.
    /// </summary>
    /// <param name="userId">The dedicated user.</param>
    /// <param name="request">What to share.</param>
    /// <returns>Nothing.</returns>
    [HttpPut("{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult SetPeer([FromRoute] Guid userId, [FromBody] SetPeerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_users.GetUserById(userId) is not { } user)
        {
            return NotFound();
        }

        var known = _catalog.ReadLibraries().Select(library => library.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (IsAdministrator(user) || request.MaxStreams < 1 || !request.SharedLibraryIds.All(known.Contains))
        {
            return BadRequest();
        }

        var changed = false;
        var lostLibrary = false;
        _store.Update(config =>
        {
            var peer = config.Peers.FirstOrDefault(candidate => candidate.UserId == userId);
            if (peer is null)
            {
                peer = new ProviderPeerConfig { UserId = userId };
                config.Peers.Add(peer);
            }

            changed = peer.MaxStreams != request.MaxStreams
                || !peer.SharedLibraryIds.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(request.SharedLibraryIds);
            lostLibrary = peer.SharedLibraryIds.Except(request.SharedLibraryIds, StringComparer.OrdinalIgnoreCase).Any();
            peer.MaxStreams = request.MaxStreams;
            peer.SharedLibraryIds.Clear();
            foreach (var id in request.SharedLibraryIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                peer.SharedLibraryIds.Add(id);
            }
        });

        // What a library took away goes now. Anything else leaves running streams alone.
        if (lostLibrary)
        {
            var caller = new PeerCaller(new PeerGrant(userId, request.SharedLibraryIds.ToHashSet(StringComparer.OrdinalIgnoreCase), request.MaxStreams), user);
            _slots.Revoke(userId, item => _catalog.FindVisible(caller, item) is not null);
        }

        if (changed)
        {
            _slots.ReleaseAll(userId);
        }

        return NoContent();
    }

    /// <summary>
    /// Disconnects a server and cuts what it is playing. Its user stays, as an ordinary user.
    /// </summary>
    /// <param name="userId">The dedicated user.</param>
    /// <returns>Nothing.</returns>
    [HttpDelete("{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RemovePeer([FromRoute] Guid userId)
    {
        var removed = false;
        _store.Update(config =>
        {
            if (config.Peers.FirstOrDefault(candidate => candidate.UserId == userId) is { } peer)
            {
                removed = config.Peers.Remove(peer);
            }
        });
        if (!removed)
        {
            return NotFound();
        }

        _slots.Revoke(userId, _ => false);
        _slots.ReleaseAll(userId);
        return NoContent();
    }

    private static bool IsAdministrator(User user) => user.HasPermission(PermissionKind.IsAdministrator);
}
