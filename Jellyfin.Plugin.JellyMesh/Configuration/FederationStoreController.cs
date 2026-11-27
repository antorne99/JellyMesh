using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
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
    private readonly IStubRegistry _stubs;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationStoreController"/> class.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    /// <param name="stubs">The stub registry, read again after a retry.</param>
    public FederationStoreController(IPluginDatabase database, IStubRegistry stubs)
    {
        _database = database;
        _stubs = stubs;
    }

    /// <summary>
    /// Gets how long a retry waits for the stub registry.
    /// </summary>
    internal TimeSpan ReloadLimit { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the state of the plugin database.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state.</returns>
    [HttpGet("Store")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<StoreStatus> GetStore(CancellationToken cancellationToken) => StatusAsync(cancellationToken);

    /// <summary>
    /// Opens the plugin database again when it is not healthy, and then reads the stub registry again.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state after the attempt.</returns>
    [HttpPost("Store/Retry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<StoreStatus> Retry(CancellationToken cancellationToken)
    {
        if (!_database.Retry().Suspended)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(ReloadLimit);
            try
            {
                await _stubs.ReloadAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The registry is read in the background instead.
            }
        }

        return await StatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the banner of the last restore off the page, once the admin has checked the servers and peers.
    /// </summary>
    /// <returns>Nothing.</returns>
    [HttpPost("Store/Recovery/Dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult DismissRecovery()
    {
        _database.DismissRecovery();
        return NoContent();
    }

    private Task<StoreStatus> StatusAsync(CancellationToken cancellationToken) => StoreStatus.ReadAsync(_database, cancellationToken);
}
