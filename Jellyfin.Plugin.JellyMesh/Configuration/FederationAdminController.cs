using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
    private readonly IManualMergeStore _merges;
    private readonly IServerLibraryManager _libraries;
    private readonly ILogger<FederationAdminController> _logger;

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
    /// <param name="merges">The manual merges, which lose the copies of a removed server.</param>
    /// <param name="libraries">The libraries the plugin keeps for each server.</param>
    /// <param name="logger">The logger.</param>
    public FederationAdminController(
        IRemoteServerRegistry registry,
        ICredentialVault vault,
        IPluginConfigurationStore store,
        IFederatedFolderLinkChecker linkChecker,
        PlaybackCoordinator playback,
        IFederatedRootSource rootSource,
        ICatalogSyncService sync,
        IManualMergeStore merges,
        IServerLibraryManager libraries,
        ILogger<FederationAdminController> logger)
    {
        _registry = registry;
        _vault = vault;
        _store = store;
        _linkChecker = linkChecker;
        _playback = playback;
        _rootSource = rootSource;
        _sync = sync;
        _merges = merges;
        _libraries = libraries;
        _logger = logger;
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
    /// Adds a server after logging in on it, and writes its folders in the federated folder.
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
        if (result.Failure == RemoteFailure.AlreadyConfigured && result.Value is { } configured)
        {
            return StatusCode(
                StatusCodes.Status409Conflict,
                new FailureResponse(result.Failure, $"This server is already configured as “{configured.Name}”.") { ServerName = configured.Name });
        }

        if (!result.IsSuccess)
        {
            return Failed(result.Failure);
        }

        _sync.PrepareServerFolders();
        return ToStatus(result.Value!);
    }

    /// <summary>
    /// Removes a server and starts deleting its content and its automatic libraries, with no confirmation (decision 140).
    /// Works whatever the state of the key. The removal is recorded before the server leaves the configuration: only
    /// recorded servers lose their content (decision 44). If the removal cannot be saved, the record is taken back and the
    /// server stays; the answer says whether its playback was already stopped.
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
        var stopping = false;
        try
        {
            // Marking may wait for a materialization, so sessions close after it, right before the server goes.
            removed = await _sync.RemoveServerAsync(
                serverId,
                async () =>
                {
                    stopping = true;
                    await _playback.CloseServerAsync(serverId).ConfigureAwait(false);
                    return _registry.Remove(serverId);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed(stopping ? RemoteFailure.RemovalFailedPlaybackStopped : RemoteFailure.RemovalFailed);
        }

        if (!removed)
        {
            return Failed(RemoteFailure.UnknownServer);
        }

        await RemoveFromMergesAsync(serverId).ConfigureAwait(false);
        _sync.StartRemovalCleanup();
        _ = _libraries.DeleteRemovedServerLibraries();
        return NoContent();
    }

    /// <summary>
    /// Forgets the servers missing from the settings that still left titles, as if they had been removed on this page, and
    /// starts deleting what they left (decision 44). A server added back to the settings wins over this.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many servers were forgotten.</returns>
    [HttpPost("MissingServers/Forget")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ForgetResponse>> ForgetMissingServers(CancellationToken cancellationToken)
    {
        int forgotten;
        try
        {
            forgotten = await _sync.ForgetMissingServersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed(RemoteFailure.ForgetFailed);
        }

        if (forgotten > 0)
        {
            _sync.StartRemovalCleanup();
        }

        return new ForgetResponse(forgotten);
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

    /// <summary>
    /// Turns on or off the Film and Serie libraries the plugin keeps for a server. Turning them off deletes them, so it
    /// needs a confirmation while there are any.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="request">The switch and the confirmation.</param>
    /// <returns>The state, while the work runs in the background.</returns>
    [HttpPost("Servers/{serverId:guid}/AutomaticLibraries")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<AutomaticLibrariesState> SetAutomaticLibraries([FromRoute] Guid serverId, [FromBody] SetAutomaticLibrariesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var answer = request.Enabled ? _libraries.Enable(serverId) : _libraries.Disable(serverId, request.Confirmed);
        if (answer.Outcome == AutomaticLibrariesOutcome.Started)
        {
            return Accepted(_libraries.GetState(serverId));
        }

        var status = answer.Outcome == AutomaticLibrariesOutcome.UnknownServer ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict;
        return StatusCode(status, new AutomaticLibrariesRefusal(answer.Outcome, RefusalMessage(answer.Outcome), answer.ToDelete));
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
            RemoteFailure.RemovalFailedPlaybackStopped => "The server could not be removed. It stays, with its titles, but any playback from it was stopped. Try again.",
            RemoteFailure.ForgetFailed => "The missing servers could not be forgotten. Their titles stay. Try again.",
            RemoteFailure.AlreadyConfigured => "This server is already configured.",
            _ => "The operation failed."
        };
    }

    internal static string RefusalMessage(AutomaticLibrariesOutcome outcome)
    {
        return outcome switch
        {
            AutomaticLibrariesOutcome.UnknownServer => "This server is not configured.",
            AutomaticLibrariesOutcome.NoFederatedFolder => "Set the federated folder first.",
            AutomaticLibrariesOutcome.ConfirmationRequired => "Confirm to delete the libraries of this server.",
            AutomaticLibrariesOutcome.Busy => "The libraries of this server are being updated. Try again when it is done.",
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

    private ServerStatus ToStatus(RemoteServerInfo server)
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
            NeedsPassword = server.NeedsPassword,
            RecognizedAs = server.RecognizedAs,
            AutomaticLibraries = _libraries.GetState(server.Id)
        };
    }

    private ObjectResult Failed(RemoteFailure failure)
    {
        var status = failure switch
        {
            RemoteFailure.Suspended or RemoteFailure.AlreadyConfigured => StatusCodes.Status409Conflict,
            RemoteFailure.UnknownServer => StatusCodes.Status404NotFound,
            RemoteFailure.InvalidServer => StatusCodes.Status400BadRequest,
            RemoteFailure.RemovalFailed or RemoteFailure.RemovalFailedPlaybackStopped or RemoteFailure.ForgetFailed => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status502BadGateway
        };

        return StatusCode(status, new FailureResponse(failure, FailureMessage(failure)));
    }

    // Takes the copies of a removed server out of the manual merges (decision 45). The server is gone by now, so a failure
    // is only logged and the merges show its copies as not found, as before.
    private async Task RemoveFromMergesAsync(Guid serverId)
    {
        var server = serverId.ToString("N");
        if (!_merges.Merges.Any(merge => ManualMergeService.HasCopyOn(merge, server)))
        {
            return;
        }

        try
        {
            await _merges.UpdateAsync(current => ManualMergeService.WithoutServer(current, server), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a path or an address.
            _logger.LogError("Could not take the removed server out of the manual merges: {Error}", exception.GetType().Name);
        }
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
