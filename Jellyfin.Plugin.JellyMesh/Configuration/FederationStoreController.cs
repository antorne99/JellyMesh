using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The plugin database on the page, for administrators only: why it is suspended and "Retry" (row 427), the banner of a
/// restore from a copy (row 466) and the files of the older version (row 469). It answers while the database is suspended.
/// </summary>
[ApiController]
[Route("JellyMesh/Admin")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class FederationStoreController : ControllerBase
{
    private readonly IPluginDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationStoreController"/> class.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    public FederationStoreController(IPluginDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Gets the state of the plugin database.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state.</returns>
    [HttpGet("Store")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<StoreStatus> GetStore(CancellationToken cancellationToken) => StatusAsync(cancellationToken);

    /// <summary>
    /// Opens the plugin database again when it is not healthy.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state after the attempt.</returns>
    [HttpPost("Store/Retry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<StoreStatus> Retry(CancellationToken cancellationToken)
    {
        _database.Retry();
        return StatusAsync(cancellationToken);
    }

    /// <summary>
    /// Takes the banner of the last restore off the page, once the admin has checked the server list.
    /// </summary>
    /// <returns>Nothing.</returns>
    [HttpPost("Store/Recovery/Dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult DismissRecovery()
    {
        _database.DismissRecovery();
        return NoContent();
    }

    private async Task<StoreStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var health = _database.Health;
        DateTimeOffset? kept = null;
        if (!health.Suspended)
        {
            try
            {
                kept = await _database.MigratedKeptUntilAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (StoreSuspendedException)
            {
                // Suspended since the health was read: the old files are not shown.
            }
        }

        return new StoreStatus(health.State, health.Reason, _database.Recovery, kept);
    }
}
