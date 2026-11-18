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
using Jellyfin.Plugin.JellyMesh.Storage;
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
    private readonly IWatchStateKeeper? _keeper;

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
    /// <param name="keeper">The watch state kept for removed servers, for the removals it holds.</param>
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
        IServerRenamer? renamer = null,
        IWatchStateKeeper? keeper = null)
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
        _keeper = keeper;
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
                .ToList()),
            HeldRemovals = _keeper?.HeldRemovals ?? []
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
        if (request.LibraryMode is { } asked && !Enum.IsDefined(asked))
        {
            return BadRequest(new SettingRefusal(nameof(AddServerRequest.LibraryMode), "Choose a library mode: ByType, ByRemoteLibrary or None."));
        }

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

        // The mode is kept at once, so that a check makes the libraries once the folder is set and a sync read the remote ones.
        var mode = request.LibraryMode ?? _store.Read(config => config.NewServerLibraryMode);
        if (mode != LibraryMode.None)
        {
            var id = result.Value!.Id;
            KeepLibraryMode(id, mode);
            _libraries.Enable(id, mode);
        }

        return ToStatus(result.Value!);
    }

    /// <summary>
    /// Removes a server and starts deleting its content and its automatic libraries, with no confirmation (decision 140).
    /// Works whatever the state of the key. The removal is recorded before the server leaves the configuration: only
    /// recorded servers lose their content (decision 44). If the removal cannot be saved, the record is taken back and the
    /// server stays; the answer says whether its playback was already stopped. What plays from it ends first, and its
    /// login on the remote last, within a short time (413).
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

        // Edits wait for the removal and are refused, and the removal waits for the edit in progress (row 434).
        using var removal = await _registry.BeginRemovalAsync(serverId, cancellationToken).ConfigureAwait(false);
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
                    return await _registry.RemoveAsync(serverId).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The server stays, so it may play again.
            if (stopping)
            {
                _playback.ReopenServer(serverId);
            }

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
        catch (StoreSuspendedException)
        {
            return StoreUnavailableFilter.Unavailable();
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
    /// Checks an edit of a server without saving anything: logs in with the new data, accepts only the saved Server Id,
    /// reads the libraries the user sees and lists the consequences of the save (row 432, 433).
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="request">The edit form.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The libraries and the consequences, or the refusal with the field it is about.</returns>
    [HttpPost("Servers/{serverId:guid}/Edit/Check")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ServerEditCheck>> CheckServerEdit([FromRoute] Guid serverId, [FromBody] ServerEditRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var edit = await _registry.EnterEditAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (edit is null)
        {
            return Failed(RemoteFailure.BeingRemoved);
        }

        var retryRename = request.Name is not null && RenameFailed(serverId);
        request = WithSavedFields(serverId, request);
        if ((RefuseName(serverId, request.Name) ?? RefuseMode(serverId, request.LibraryMode)) is { } refused)
        {
            return refused;
        }

        var result = await _registry.CheckEditAsync(serverId, request.ToEdit(), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? ToCheck(serverId, request, result.Value!, retryRename) : EditFailed(result);
    }

    /// <summary>
    /// Saves an edit of a server all or nothing, after the same checks as its check (row 432), then stops the streams and
    /// ends the login of an old user, renames, writes the server folders and starts a sync of the server as needed (row 434).
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="request">The edit form.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The saved server, or the refusal with the field it is about.</returns>
    [HttpPut("Servers/{serverId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ServerStatus>> SaveServerEdit([FromRoute] Guid serverId, [FromBody] ServerEditRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var edit = await _registry.EnterEditAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (edit is null)
        {
            return Failed(RemoteFailure.BeingRemoved);
        }

        return await SaveEditCoreAsync(serverId, request, cancellationToken).ConfigureAwait(false);
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
    /// Drops the stub moves of a server that wait for a scan, and deletes their old files (§10 row 288).
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state of the rename.</returns>
    [HttpPost("Servers/{serverId:guid}/Rename/Cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ServerRenameState>> CancelServerRename([FromRoute] Guid serverId, CancellationToken cancellationToken)
    {
        if (_renamer is null)
        {
            return NotFound();
        }

        using var edit = await _registry.EnterEditAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (edit is null)
        {
            return Failed(RemoteFailure.BeingRemoved);
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

    internal static string EditFailureMessage(RemoteFailure failure, RemoteEditField field, string? serverName)
    {
        return (failure, field) switch
        {
            (RemoteFailure.AlreadyConfigured, _) when !string.IsNullOrEmpty(serverName) => $"This server is already configured as “{serverName}”.",
            (RemoteFailure.InvalidServer, RemoteEditField.Name) => "Enter a name.",
            (RemoteFailure.InvalidServer, RemoteEditField.Url) => "Enter an http or https URL with no credentials, query or fragment.",
            (RemoteFailure.InvalidServer, RemoteEditField.Username) => "Enter the dedicated user: the saved one can no longer be read.",
            (RemoteFailure.InvalidServer, RemoteEditField.Password) => "Enter the password of the dedicated user: the saved one can no longer be read.",
            (RemoteFailure.InvalidServer, RemoteEditField.LibraryMode) => "Choose a library mode.",
            _ => FailureMessage(failure)
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
            RemoteFailure.LibraryUnavailable => "A chosen library is no longer visible to the dedicated user. Open Edit and save the libraries again.",
            RemoteFailure.CatalogChanged => "The catalog kept changing while it was read. Try again.",
            RemoteFailure.StreamLimitReached => "This server is at its stream limit on that remote.",
            RemoteFailure.RemovalFailed => "The server could not be removed. It stays, with its titles. Try again.",
            RemoteFailure.RemovalFailedPlaybackStopped => "The server could not be removed. It stays, with its titles, but any playback from it was stopped. Try again.",
            RemoteFailure.ForgetFailed => "The missing servers could not be forgotten. Their titles stay. Try again.",
            RemoteFailure.AlreadyConfigured => "This server is already configured.",
            RemoteFailure.OtherServer => "Another server answers at this address. To use it, remove this one and add it as a new server.",
            RemoteFailure.BeingRemoved => "This server is being removed.",
            RemoteFailure.NameTaken => "Another server already has this name.",
            RemoteFailure.RenameUnfinished => "A rename of this server has not finished. Wait for it, or save the edit without changing the name.",
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
            AutomaticLibrariesOutcome.ModeChangeNotAllowed => "The library mode of this server cannot be changed here.",
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
            AutomaticLibraries = _libraries.GetState(server.Id),
            Rename = _renamer?.GetState(server.Id)
        };
    }

    private static int StatusOf(RemoteFailure failure)
    {
        return failure switch
        {
            RemoteFailure.Suspended or RemoteFailure.AlreadyConfigured or RemoteFailure.OtherServer or RemoteFailure.BeingRemoved
                or RemoteFailure.NameTaken or RemoteFailure.RenameUnfinished => StatusCodes.Status409Conflict,
            RemoteFailure.UnknownServer => StatusCodes.Status404NotFound,
            RemoteFailure.InvalidServer => StatusCodes.Status400BadRequest,
            RemoteFailure.RemovalFailed or RemoteFailure.RemovalFailedPlaybackStopped or RemoteFailure.ForgetFailed => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status502BadGateway
        };
    }

    private ObjectResult Failed(RemoteFailure failure)
    {
        return StatusCode(StatusOf(failure), new FailureResponse(failure, FailureMessage(failure)));
    }

    // A refused edit, with the field it is about so that the page shows it there (row 432).
    private ObjectResult EditFailed(RemoteResult<RemoteEditReport> result)
    {
        var field = result.Value?.Field ?? RemoteEditField.None;
        var name = result.Value?.ServerName;
        return StatusCode(
            StatusOf(result.Failure),
            new FailureResponse(result.Failure, EditFailureMessage(result.Failure, field, name))
            {
                ServerName = string.IsNullOrEmpty(name) ? null : name,
                Field = field == RemoteEditField.None ? null : field
            });
    }

    // Refuses on the name a new name another server has, or one given while a rename has not finished (row 434).
    // Tells whether a server other than this one has the name, trimmed and in any case.
    private static bool NameHeldElsewhere(IReadOnlyList<RemoteServerInfo> servers, Guid serverId, string? name)
        => !string.IsNullOrWhiteSpace(name)
            && servers.Any(server => server.Id != serverId && string.Equals(server.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    private ObjectResult? RefuseName(Guid serverId, string? name)
    {
        var trimmed = name?.Trim();
        var servers = _registry.GetServers();
        if (string.IsNullOrEmpty(trimmed) || servers.FirstOrDefault(server => server.Id == serverId) is not { } saved || saved.Name == trimmed)
        {
            return null;
        }

        var failure = NameHeldElsewhere(servers, serverId, trimmed)
            ? RemoteFailure.NameTaken
            : RenameUnfinished(serverId) ? RemoteFailure.RenameUnfinished : RemoteFailure.None;
        return failure == RemoteFailure.None
            ? null
            : EditFailed(RemoteResult.Fail(failure, new RemoteEditReport(RemoteEditField.Name)));
    }

    // Tells whether a rename of the server runs, waits for a scan, failed, or left a library waiting for a restart.
    private bool RenameUnfinished(Guid serverId)
    {
        if (_renamer is null)
        {
            return false;
        }

        var state = _renamer.GetState(serverId);
        return state.Busy || state.Pending || state.Failed || _renamer.NeedsRestart;
    }

    // Tells whether the last rename of the server failed, so that a save of the same name runs it again.
    private bool RenameFailed(Guid serverId) => _renamer is not null && _renamer.GetState(serverId).Failed;

    // Refuses on the mode an unknown one and a change while the libraries are being updated (section 3.8).
    private ObjectResult? RefuseMode(Guid serverId, LibraryMode? asked)
    {
        if (asked is not { } mode)
        {
            return null;
        }

        if (!Enum.IsDefined(mode))
        {
            return EditFailed(RemoteResult.Fail(RemoteFailure.InvalidServer, new RemoteEditReport(RemoteEditField.LibraryMode)));
        }

        var saved = _store.Read(config => config.Servers.FirstOrDefault(server => server.Id == serverId)?.LibraryMode);
        if (saved is null || (mode == saved && mode == LibraryMode.None))
        {
            return null;
        }

        var state = _libraries.GetState(serverId);
        return state.Busy
            ? StatusCode(StatusCodes.Status409Conflict, new FailureResponse(RemoteFailure.None, RefusalMessage(AutomaticLibrariesOutcome.Busy)) { Field = RemoteEditField.LibraryMode })
            : null;
    }

    // Fills a name or an address the form left out with the saved one, so that a form of the mode alone changes neither.
    private ServerEditRequest WithSavedFields(Guid serverId, ServerEditRequest request)
    {
        if ((request.Name is not null && request.Url is not null)
            || _registry.GetServers().FirstOrDefault(server => server.Id == serverId) is not { } saved)
        {
            return request;
        }

        return new ServerEditRequest
        {
            Name = request.Name ?? saved.Name,
            Url = request.Url ?? saved.Url,
            Username = request.Username,
            Password = request.Password,
            LibraryIds = request.LibraryIds,
            LibraryMode = request.LibraryMode
        };
    }

    // Closes the playback of a server whose user changed, then lets it play again with the new user (row 434).
    private async Task StopStreamsAsync(Guid serverId)
    {
        try
        {
            await _playback.CloseServerAsync(serverId).ConfigureAwait(false);
        }
        finally
        {
            _playback.ReopenServer(serverId);
        }
    }

    // Saves an edit after refusing a taken name, then sets off its effects (row 434). The caller holds the edit lock.
    private async Task<ActionResult<ServerStatus>> SaveEditCoreAsync(Guid serverId, ServerEditRequest request, CancellationToken cancellationToken)
    {
        // A form with no name runs no failed rename again: only a save of the name does (row 290).
        var retryRename = request.Name is not null && RenameFailed(serverId);
        request = WithSavedFields(serverId, request);

        // A first check, before any remote; the registry checks the name again in the change that stores it.
        if ((RefuseName(serverId, request.Name) ?? RefuseMode(serverId, request.LibraryMode)) is { } refused)
        {
            return refused;
        }

        var result = await _registry.SaveEditAsync(serverId, request.ToEdit(), () => StopStreamsAsync(serverId), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return EditFailed(result);
        }

        AfterSave(serverId, result.Value!.Changes, request.Name!.Trim(), retryRename, request.LibraryMode);

        var server = _registry.GetServers().FirstOrDefault(candidate => candidate.Id == serverId);
        return server is null ? Failed(RemoteFailure.UnknownServer) : ToStatus(server) with { RemoteWarning = result.Value.RemoteWarning };
    }

    // Runs what follows a saved edit, after the old user was logged out by the save: rename, libraries, mode,
    // then the sync of the server. A step that fails is logged and leaves the save and the next steps (rows 432, 434).
    private void AfterSave(Guid serverId, RemoteEditChanges changes, string name, bool retryRename, LibraryMode? mode)
    {
        // A rename that failed runs again on a save of the same name, as the page says (row 290).
        if ((changes.HasFlag(RemoteEditChanges.Name) || retryRename) && _renamer is not null)
        {
            RunStep("rename", () =>
            {
                // The name is saved and was checked before the save, so the rename needs no confirmation here.
                var answer = _renamer.Rename(serverId, name, confirmed: true);
                if (answer.Outcome != ServerRenameOutcome.Started)
                {
                    _logger.LogWarning("The rename after an edit of a server did not start: {Outcome}", answer.Outcome);
                }
            });
        }

        if (changes.HasFlag(RemoteEditChanges.Libraries))
        {
            RunStep("libraries", _sync.PrepareServerFolders);
        }

        if (changes.HasFlag(RemoteEditChanges.LibraryMode))
        {
            RunStep("library mode", () =>
            {
                var answer = _libraries.ApplyMode(serverId);
                if (answer.Outcome != AutomaticLibrariesOutcome.Started)
                {
                    _logger.LogWarning("The change of library mode after an edit of a server did not start: {Outcome}", answer.Outcome);
                }
            });
        }
        else if (mode is { } asked and not LibraryMode.None)
        {
            RunStep("library mode", () => MakeMissingLibraries(serverId, asked));
        }

        if ((changes & (RemoteEditChanges.Url | RemoteEditChanges.User | RemoteEditChanges.Libraries)) != RemoteEditChanges.None)
        {
            RunStep("sync", () => _sync.StartServerSync(serverId));
        }
    }

    // The saved mode sent again makes the libraries of the server that are missing (section 3.8).
    private void MakeMissingLibraries(Guid serverId, LibraryMode mode)
    {
        if (_store.Read(config => config.Servers.FirstOrDefault(server => server.Id == serverId)?.LibraryMode) != mode)
        {
            return;
        }

        var answer = _libraries.Enable(serverId, mode);
        if (answer.Outcome != AutomaticLibrariesOutcome.Started)
        {
            _logger.LogWarning("The library mode after an edit of a server was not applied: {Outcome}", answer.Outcome);
        }
    }

    // Stores the mode of a server at once, so that a check makes its libraries once the folder is set.
    private void KeepLibraryMode(Guid serverId, LibraryMode mode)
    {
        _store.Update(config =>
        {
            if (config.Servers.FirstOrDefault(server => server.Id == serverId) is { } server)
            {
                server.LibraryMode = mode;
            }
        });
    }

    private void RunStep(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            // The type only: a message could hold a path or an address.
            _logger.LogError("The {Step} step after an edit of a server failed: {Error}", step, exception.GetType().Name);
        }
    }

    // The libraries and the consequences of an edit the remote accepted (row 432).
    private ServerEditCheck ToCheck(Guid serverId, ServerEditRequest request, RemoteEditReport report, bool retryRename)
    {
        var name = request.Name!.Trim();
        var server = _registry.GetServers().FirstOrDefault(candidate => candidate.Id == serverId);
        var visible = report.Libraries ?? [];
        var kept = report.LibraryIds.ToHashSet(StringComparer.Ordinal);
        var removed = (server?.ImportedLibraryIds ?? [])
            .Where(id => !kept.Contains(id))
            .Select(id => visible.FirstOrDefault(library => library.Id == id) is { } library
                ? new ImportedLibraryRemoval(id, library.Name, false)
                : new ImportedLibraryRemoval(id, null, true))
            .ToList();
        var rename = server is not null && (report.Changes.HasFlag(RemoteEditChanges.Name) || retryRename)
            ? new ServerRenameConsequence(server.Name, name, _libraries.GetState(serverId).Libraries.Where(library => !library.Adopted).Select(library => library.Name).ToList())
            : null;
        var streams = report.Changes.HasFlag(RemoteEditChanges.User) ? _playback.CountServerSessions(serverId) : 0;
        return new ServerEditCheck(
            report.ServerName ?? string.Empty,
            visible.Select(library => new LibraryOption(library.Id, library.Name, library.CollectionType, kept.Contains(library.Id))).ToList(),
            new ServerEditConsequences(rename, removed, streams)
            {
                ModeChange = report.Changes.HasFlag(RemoteEditChanges.LibraryMode) && request.LibraryMode is { } mode ? _libraries.PreviewModeChange(serverId, mode) : null
            })
        {
            RemoteOffline = report.RemoteOffline,
            RemoteWarning = report.RemoteWarning
        };
    }

    // Takes the copies of a removed server out of the manual merges (decision 45). The server is gone by now, so a failure
    // is only logged and the merges show its copies as not found, as before.
    private async Task RemoveFromMergesAsync(Guid serverId)
    {
        var server = serverId.ToString("N");
        var held = _merges.Merges.Where(merge => ManualMergeService.HasCopyOn(merge, server)).ToList();

        // The kept watch state finds the other copies through these merges (D33), in place of those of an earlier removal of
        // the same server; unsaved, the copies stay in them.
        if (_keeper is not null && !await _keeper.KeepMergesAsync(serverId, held, CancellationToken.None).ConfigureAwait(false))
        {
            return;
        }

        if (held.Count == 0)
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
