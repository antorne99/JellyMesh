using System.Net.Mime;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The manual full sync and what it produced, for administrators only.
/// </summary>
[ApiController]
[Route("JellyMesh/Admin")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class FederationSyncController : ControllerBase
{
    private readonly ICatalogSyncService _sync;
    private readonly IFederationResolver _resolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationSyncController"/> class.
    /// </summary>
    /// <param name="sync">The sync service.</param>
    /// <param name="resolver">The resolver.</param>
    public FederationSyncController(ICatalogSyncService sync, IFederationResolver resolver)
    {
        _sync = sync;
        _resolver = resolver;
    }

    /// <summary>
    /// Starts a full sync of the chosen libraries. It runs in the background: poll <see cref="GetSync"/>.
    /// </summary>
    /// <returns>The state of the sync.</returns>
    [HttpPost("Sync")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<SyncStatus> StartSync()
    {
        return _sync.TryStart() switch
        {
            SyncStartResult.Started => Accepted(_sync.GetStatus()),
            SyncStartResult.AlreadyRunning => Conflict(new FailureResponse(RemoteFailure.None, "A sync is already running.")),
            _ => Conflict(new FailureResponse(RemoteFailure.Suspended, FederationAdminController.FailureMessage(RemoteFailure.Suspended)))
        };
    }

    /// <summary>
    /// Gets the state of the sync.
    /// </summary>
    /// <returns>The state.</returns>
    [HttpGet("Sync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SyncStatus> GetSync() => _sync.GetStatus();

    /// <summary>
    /// Gets the sources of each content identity, worked out now from the index and the local library.
    /// </summary>
    /// <returns>The view.</returns>
    [HttpGet("Index/Decisions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<FederationView> GetDecisions() => _resolver.Resolve();
}
