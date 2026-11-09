using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Backs the configuration page, for administrators only. Passwords come in here and never go out.
/// </summary>
[ApiController]
[Route("JellyMesh/Admin")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class FederationAdminController : ControllerBase
{
    private readonly IRemoteServerRegistry _registry;
    private readonly ICredentialVault _vault;
    private readonly IPluginConfigurationStore _store;
    private readonly IFederatedFolderLinkChecker _linkChecker;
    private readonly PlaybackCoordinator _playback;
    private readonly IFederatedRootSource _rootSource;
    private readonly ICatalogSyncService _sync;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationAdminController"/> class.
    /// </summary>
    /// <param name="registry">The server registry.</param>
    /// <param name="vault">The credential vault.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="linkChecker">The federated folder link checker.</param>
    /// <param name="playback">The playback coordinator.</param>
    /// <param name="rootSource">The source of the federated folder.</param>
    /// <param name="sync">The sync service, which deletes what a removed server left.</param>
    public FederationAdminController(
        IRemoteServerRegistry registry,
        ICredentialVault vault,
        IPluginConfigurationStore store,
        IFederatedFolderLinkChecker linkChecker,
        PlaybackCoordinator playback,
        IFederatedRootSource rootSource,
        ICatalogSyncService sync)
    {
        _registry = registry;
        _vault = vault;
        _store = store;
        _linkChecker = linkChecker;
        _playback = playback;
        _rootSource = rootSource;
        _sync = sync;
    }

    /// <summary>
    /// Gets the state shown on the configuration page.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<FederationStatus> GetStatus()
    {
        var keyState = _vault.GetKeyState();
        return new FederationStatus(
            keyState,
            keyState != KeyState.Ok,
            KeyWarning(keyState),
            GetFederatedFolder(),
            _registry.GetServers().Select(ToStatus).ToList())
        {
            LegacyKeySet = _vault.IsLegacyKeySet
        };
    }

    /// <summary>
    /// Adds a server after logging in on it.
    /// </summary>
    /// <param name="request">The server and the dedicated user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The added server.</returns>
    [HttpPost("Servers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ServerStatus>> AddServer([FromBody] AddServerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var credentials = new RemoteCredentials { Username = request.Username, Password = request.Password };
        var result = await _registry.AddAsync(request.Name, request.Url, credentials, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? ToStatus(result.Value!) : Failed(result.Failure);
    }

    /// <summary>
    /// Removes a server and starts deleting its content. Works whatever the state of the key. The removal is recorded before
    /// the server leaves the configuration: only recorded servers lose their content (decision 44). If the removal cannot be
    /// saved, the record is taken back and the server stays.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Nothing.</returns>
    [HttpDelete("Servers/{serverId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult> RemoveServer([FromRoute] Guid serverId, CancellationToken cancellationToken)
    {
        if (_registry.GetServers().All(server => server.Id != serverId))
        {
            return Failed(RemoteFailure.UnknownServer);
        }

        bool removed;
        try
        {
            // Marking may wait for a materialization, so sessions close after it, right before the server goes.
            removed = await _sync.RemoveServerAsync(
                serverId,
                async () =>
                {
                    await _playback.CloseServerAsync(serverId).ConfigureAwait(false);
                    return _registry.Remove(serverId);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed(RemoteFailure.RemovalFailed);
        }

        if (!removed)
        {
            return Failed(RemoteFailure.UnknownServer);
        }

        _sync.StartRemovalCleanup();
        return NoContent();
    }

    /// <summary>
    /// Enters the dedicated user of a server again, after logging in with it. This is how a lost key file is fixed.
    /// </summary>
    /// <param name="serverId">The server to update.</param>
    /// <param name="request">The dedicated user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The server with its new credentials.</returns>
    [HttpPost("Servers/{serverId:guid}/Credentials")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ServerStatus>> UpdateCredentials([FromRoute] Guid serverId, [FromBody] CredentialsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var credentials = new RemoteCredentials { Username = request.Username, Password = request.Password };
        var result = await _registry.UpdateCredentialsAsync(serverId, credentials, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? ToStatus(result.Value!) : Failed(result.Failure);
    }

    /// <summary>
    /// Tests the connection to a server and refreshes its capabilities.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report.</returns>
    [HttpPost("Servers/{serverId:guid}/Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ConnectionReport>> TestServer([FromRoute] Guid serverId, CancellationToken cancellationToken)
    {
        var result = await _registry.TestAsync(serverId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.Value! : Failed(result.Failure);
    }

    /// <summary>
    /// Lists the importable libraries of a server.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The libraries.</returns>
    [HttpGet("Servers/{serverId:guid}/Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<IReadOnlyList<LibraryOption>>> GetLibraries([FromRoute] Guid serverId, CancellationToken cancellationToken)
    {
        var result = await _registry.GetLibrariesAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Failed(result.Failure);
        }

        var selected = _registry.GetServers().FirstOrDefault(server => server.Id == serverId)?.ImportedLibraryIds ?? [];
        return result.Value!
            .Select(library => new LibraryOption(library.Id, library.Name, library.CollectionType, selected.Contains(library.Id)))
            .ToList();
    }

    /// <summary>
    /// Sets the libraries to import from a server.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="request">The libraries.</param>
    /// <returns>Nothing.</returns>
    [HttpPost("Servers/{serverId:guid}/Libraries")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult SetLibraries([FromRoute] Guid serverId, [FromBody] SetLibrariesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _registry.SetImportedLibraries(serverId, request.LibraryIds) ? NoContent() : Failed(RemoteFailure.UnknownServer);
    }

    internal static string? KeyWarning(KeyState state)
    {
        return state switch
        {
            KeyState.Ok => null,
            KeyState.Missing => "The plugin cannot keep its key file in its data folder. Remote credentials cannot be saved or used, and sync and remote playback are suspended.",
            _ => "The saved passwords do not decrypt with the key file: it was lost or replaced, or they were altered. Sync and remote playback are suspended until the password of each marked server is entered again."
        };
    }

    internal static string FailureMessage(RemoteFailure failure)
    {
        return failure switch
        {
            RemoteFailure.Suspended => "Remote servers are suspended until the secret key is fixed.",
            RemoteFailure.UnknownServer => "This server is not configured.",
            RemoteFailure.InvalidServer => "Enter a name, a user and an http or https URL with no credentials, query or fragment.",
            RemoteFailure.Unreachable => "The server cannot be reached.",
            RemoteFailure.Timeout => "The server did not answer in time.",
            RemoteFailure.AuthenticationFailed => "The server refused the login.",
            RemoteFailure.Forbidden => "The server does not allow this for the dedicated user.",
            RemoteFailure.AdministratorAccount => "This account is an administrator. Use a dedicated non-admin user.",
            RemoteFailure.RedirectRefused => "The server redirected somewhere that is not followed. Check the URL.",
            RemoteFailure.ServerError => "The server answered with an error.",
            RemoteFailure.InvalidResponse => "The answer does not look like a Jellyfin server.",
            RemoteFailure.ResponseTooLarge => "The server sent an answer too large to accept.",
            RemoteFailure.LibraryUnavailable => "A chosen library is no longer visible to the dedicated user. Open Libraries and save the selection again.",
            RemoteFailure.CatalogChanged => "The catalog kept changing while it was read. Try again.",
            RemoteFailure.StreamLimitReached => "This server is at its stream limit on that remote.",
            RemoteFailure.RemovalFailed => "The server could not be removed. It stays, with its titles. Try again.",
            _ => "The operation failed."
        };
    }

    private static string ConnectionOf(RemoteFailure? failure)
    {
        return failure switch
        {
            null => "NotChecked",
            RemoteFailure.None or RemoteFailure.StreamLimitReached => "Online",
            RemoteFailure.Unreachable or RemoteFailure.Timeout => "Offline",
            _ => failure.Value.ToString()
        };
    }

    private static ServerStatus ToStatus(RemoteServerInfo server)
    {
        return new ServerStatus(
            server.Id,
            server.Name,
            server.Url,
            server.Mode,
            server.ProtocolVersion,
            server.ImportedLibraryIds,
            ConnectionOf(server.LastFailure),
            server.LastCheckedUtc)
        {
            NeedsPassword = server.NeedsPassword
        };
    }

    private ObjectResult Failed(RemoteFailure failure)
    {
        var status = failure switch
        {
            RemoteFailure.Suspended => StatusCodes.Status409Conflict,
            RemoteFailure.UnknownServer => StatusCodes.Status404NotFound,
            RemoteFailure.InvalidServer => StatusCodes.Status400BadRequest,
            RemoteFailure.RemovalFailed => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status502BadGateway
        };

        return StatusCode(status, new FailureResponse(failure, FailureMessage(failure)));
    }

    private FederatedFolderStatus GetFederatedFolder()
    {
        var path = _store.Read(config => config.FederatedFolderPath);
        var report = path.Length == 0 ? FederatedFolderLinkReport.NotChecked : _linkChecker.Check(path);
        return new FederatedFolderStatus(
            path,
            path.Length == 0 ? string.Empty : FederatedFolderLayout.MoviesPath(path),
            path.Length == 0 ? string.Empty : FederatedFolderLayout.SeriesPath(path),
            report.State,
            report.Movies,
            report.Series,
            report.SeriesGroupingOff,
            PluginConfiguration.FederatedRootOf(_rootSource.GetFederatedRoot()) is not null);
    }
}
