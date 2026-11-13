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
    /// <summary>The refusal of a hide whose libraries are no longer listed.</summary>
    internal const string ReleasedLibraryNotListed = "These libraries are no longer listed. The page reads them again.";

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
    private readonly ILayoutMigration? _migration;
    private readonly ILibraryChangeTracker? _changes;
    private readonly IServerRenamer? _renamer;

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
    /// <param name="migration">The manual migration from the flat layout, for the banner.</param>
    /// <param name="changes">The libraries that changed on the federated folder since the last sync.</param>
    /// <param name="renamer">The server rename.</param>
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
        ILogger<FederationAdminController> logger,
        ILayoutMigration? migration = null,
        ILibraryChangeTracker? changes = null,
        IServerRenamer? renamer = null)
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
        _migration = migration;
        _changes = changes;
        _renamer = renamer;
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

        // A server id saved twice, as by a hand-edited configuration, shows once with its first entry.
        var servers = _registry.GetServers().DistinctBy(server => server.Id).ToList();
        return new FederationStatus(
            keyState,
            keyState != KeyState.Ok,
            KeyWarning(keyState),
            GetFederatedFolder(servers),
            servers.Select(ToStatus).ToList())
        {
            LegacyKeySet = _vault.IsLegacyKeySet,
            RenamedLibrariesNeedRestart = _renamer?.NeedsRestart ?? false,
            ReleasedLibraries = _store.Read(config => config.RemovedServerLibraries
                .SelectMany(held => held.ReleasedLibraries
                    .Where(name => !held.HiddenReleasedLibraries.Contains(name, StringComparer.Ordinal))
                    .Select(name => new ReleasedLibrary(held.ServerId, name)))
                .ToList())
        };
    }

    /// <summary>
    /// Takes released libraries of removed servers off the page (§10 row 281). The libraries themselves are not touched.
    /// Hides nothing if one of them is not listed.
    /// </summary>
    /// <param name="request">The libraries.</param>
    /// <returns>Nothing.</returns>
    [HttpPost("ReleasedLibraries/Hide")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult HideReleasedLibraries([FromBody] HideReleasedLibrariesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var hidden = false;
        _store.Update(config =>
        {
            var found = request.Libraries
                .Select(library => (Held: config.RemovedServerLibraries.FirstOrDefault(held => held.ServerId == library.ServerId), library.Name))
                .ToList();
            if (found.Any(pair => pair.Held is null || !pair.Held.ReleasedLibraries.Contains(pair.Name, StringComparer.Ordinal)))
            {
                return;
            }

            foreach (var (held, name) in found.Where(pair => !pair.Held!.HiddenReleasedLibraries.Contains(pair.Name, StringComparer.Ordinal)))
            {
                held!.HiddenReleasedLibraries.Add(name);
            }

            hidden = true;
        });

        return hidden ? NoContent() : NotFound(new FailureResponse(RemoteFailure.None, ReleasedLibraryNotListed));
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
        _ = DeleteLibrariesAfterAsync(_sync.StartRemovalCleanup());
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
            _ = _sync.StartRemovalCleanup();
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

    /// <summary>
    /// Renames a server with its stubs and automatic libraries, after a confirmation (Q38).
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="request">The new name and the confirmation.</param>
    /// <returns>The state, while the work runs in the background.</returns>
    [HttpPost("Servers/{serverId:guid}/Rename")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<ServerRenameState> RenameServer([FromRoute] Guid serverId, [FromBody] RenameServerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_renamer is null)
        {
            return NotFound();
        }

        var answer = _renamer.Rename(serverId, request.Name, request.Confirmed);
        if (answer.Outcome == ServerRenameOutcome.Started)
        {
            return Accepted(_renamer.GetState(serverId));
        }

        var status = answer.Outcome switch
        {
            ServerRenameOutcome.InvalidName => StatusCodes.Status400BadRequest,
            ServerRenameOutcome.UnknownServer => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict
        };
        return StatusCode(status, new ServerRenameRefusal(answer.Outcome, RenameRefusalMessage(answer.Outcome), answer.Libraries));
    }

    /// <summary>
    /// Drops the stub moves of a server that wait for a scan, and deletes their old files (§10 row 288).
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>The state of the rename.</returns>
    [HttpPost("Servers/{serverId:guid}/Rename/Cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ServerRenameState>> CancelServerRename([FromRoute] Guid serverId)
    {
        if (_renamer is null)
        {
            return NotFound();
        }

        var outcome = await _renamer.CancelPendingAsync(serverId).ConfigureAwait(false);
        if (outcome == ServerRenameOutcome.Cancelled)
        {
            return _renamer.GetState(serverId);
        }

        var status = outcome switch
        {
            ServerRenameOutcome.UnknownServer => StatusCodes.Status404NotFound,
            ServerRenameOutcome.CancelFailed => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status409Conflict
        };
        return StatusCode(status, new ServerRenameRefusal(outcome, RenameRefusalMessage(outcome), []));
    }

    /// <summary>
    /// Records the choice in the migration banner: manual, or automatic, which starts or resumes in the background (Q55, Q57).
    /// </summary>
    /// <param name="request">The mode chosen.</param>
    /// <returns>The migration status.</returns>
    [HttpPost("Migration")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<LayoutMigrationStatus?> ChooseMigration([FromBody] ChooseMigrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = _store.Read(config => config.FederatedFolderPath);
        if (_migration is null || string.IsNullOrEmpty(path))
        {
            return NotFound();
        }

        var answer = _migration.Choose(request.Mode);
        return answer.Outcome switch
        {
            LayoutMigrationChoiceOutcome.Started => Accepted(_migration.GetStatus(path)),
            LayoutMigrationChoiceOutcome.Chosen => _migration.GetStatus(path),
            _ => StatusCode(StatusCodes.Status409Conflict, new LayoutMigrationRefusal(answer.Outcome, MigrationRefusalMessage(answer.Outcome)))
        };
    }

    /// <summary>
    /// Sets from the settings the mode for the servers linked later, the field of the banner (Q58, §10 row 368). While the
    /// banner asks, it is the banner's choice.
    /// </summary>
    /// <param name="request">The mode chosen.</param>
    /// <returns>No content, accepted when it started the automatic migration, or the reason of a refusal.</returns>
    [HttpPost("Migration/NewServers")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult SetNewServerMode([FromBody] ChooseMigrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_migration is null)
        {
            return NotFound();
        }

        var answer = _migration.SetMode(request.Mode);
        return answer.Outcome switch
        {
            LayoutMigrationChoiceOutcome.Started => Accepted(),
            LayoutMigrationChoiceOutcome.Chosen => NoContent(),
            _ => StatusCode(StatusCodes.Status409Conflict, new LayoutMigrationRefusal(answer.Outcome, MigrationRefusalMessage(answer.Outcome)))
        };
    }

    internal static string MigrationRefusalMessage(LayoutMigrationChoiceOutcome outcome)
    {
        return outcome switch
        {
            LayoutMigrationChoiceOutcome.Busy => "The automatic migration is running. Try again when it stops.",
            LayoutMigrationChoiceOutcome.NothingToMigrate => "There is nothing to migrate.",
            _ => "The automatic migration cannot go back to manual."
        };
    }

    internal static string RenameRefusalMessage(ServerRenameOutcome outcome)
    {
        return outcome switch
        {
            ServerRenameOutcome.InvalidName => "Enter a name.",
            ServerRenameOutcome.UnknownServer => "This server is not configured.",
            ServerRenameOutcome.ConfirmationRequired => "Confirm to rename this server.",
            ServerRenameOutcome.Busy => "This server is being renamed, or its titles wait for a library scan. Try again when it is done.",
            ServerRenameOutcome.CancelFailed => "Some old files of this server could not be deleted. Try again.",
            _ => "The operation failed."
        };
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
            AutomaticLibraries = _libraries.GetState(server.Id),
            Rename = _renamer?.GetState(server.Id)
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

    // The stubs go first: Jellyfin hands a primary version over to the others only when its item is deleted alone.
    private async Task DeleteLibrariesAfterAsync(Task cleanup)
    {
        await cleanup.ConfigureAwait(false);
        await _libraries.DeleteRemovedServerLibraries().ConfigureAwait(false);
    }

    private FederatedFolderStatus GetFederatedFolder(IReadOnlyList<RemoteServerInfo> servers)
    {
        var path = _store.Read(config => config.FederatedFolderPath);
        var report = path.Length == 0 ? FederatedFolderLinkReport.NotChecked : _linkChecker.Check(path, servers.Select(server => server.Id).ToList());
        var names = servers.ToDictionary(server => server.Id, server => server.Name);
        return new FederatedFolderStatus(
            path,
            report.State,
            report.Servers.Select(link => ToFolderStatus(link, names[link.ServerId])).ToList(),
            report.Covering,
            report.SeriesSplit,
            PluginConfiguration.FederatedRootOf(_rootSource.GetFederatedRoot()) is not null)
        {
            Migration = path.Length == 0 ? null : _migration?.GetStatus(path),
            Changes = path.Length == 0 ? LibraryChanges.None : _changes?.Compare() ?? LibraryChanges.None,
            NewServerMode = _migration?.NewServerMode ?? LayoutMigrationMode.Manual
        };
    }

    private ServerFolderStatus ToFolderStatus(ServerFolderLink link, string name)
    {
        var automatic = _libraries.GetState(link.ServerId);
        var unregistered = automatic is { Enabled: true }
            ? link.OnlyOnItsFolders.Where(library => !automatic.Libraries.Any(own => string.Equals(own.Name, library, StringComparison.Ordinal))).ToList()
            : [];
        return new ServerFolderStatus(
            link.ServerId,
            name,
            link.MoviesPath,
            link.SeriesPath,
            link.Movies,
            link.Series,
            link.SeriesGroupingOff,
            link.SeriesLibraries,
            unregistered);
    }
}
