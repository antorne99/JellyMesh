using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Activity;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// <see cref="IRemoteServerRegistry"/> over the plugin configuration. Access tokens stay in memory;
/// after a restart the first call logs in again.
/// </summary>
public sealed class RemoteServerRegistry : IRemoteServerRegistry, IDisposable
{
    // The changes of an edit that need the remote to answer (D28).
    private const RemoteEditChanges RemoteChanges = RemoteEditChanges.Url | RemoteEditChanges.User | RemoteEditChanges.Password | RemoteEditChanges.Libraries;

    /// <summary>
    /// The longest a removal waits for the remote to end its login (413).
    /// </summary>
    public static readonly TimeSpan LogoutTimeout = TimeSpan.FromSeconds(3);

    private readonly IPluginConfigurationStore _store;
    private readonly ICredentialVault _vault;
    private readonly IJellyfinRemoteClient _client;
    private readonly ILogger<RemoteServerRegistry> _logger;
    private readonly IActivityWriter? _activity;
    private readonly ConcurrentDictionary<Guid, ServerRuntime> _runtimes = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _editGates = new();
    private readonly Lock _removingLock = new();
    private readonly Dictionary<Guid, int> _removing = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteServerRegistry"/> class.
    /// </summary>
    /// <param name="store">The configuration store.</param>
    /// <param name="vault">The credential vault.</param>
    /// <param name="client">The remote client.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="activity">Where a server going offline or answering again is written.</param>
    public RemoteServerRegistry(
        IPluginConfigurationStore store,
        ICredentialVault vault,
        IJellyfinRemoteClient client,
        ILogger<RemoteServerRegistry> logger,
        IActivityWriter? activity = null)
    {
        _store = store;
        _vault = vault;
        _client = client;
        _logger = logger;
        _activity = activity;
    }

    /// <summary>
    /// Gets the longest a removal waits for the logout. Shorter in tests.
    /// </summary>
    internal TimeSpan LogoutLimit { get; init; } = LogoutTimeout;

    // Whether the registry holds in-memory state for a server. Read by tests.
    internal bool HoldsStateFor(Guid serverId) => _runtimes.ContainsKey(serverId);

    /// <inheritdoc />
    public IReadOnlyList<RemoteServerInfo> GetServers()
    {
        return _store.Read(config => config.Servers.Select(ToInfo).ToList());
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteServerInfo>> AddAsync(string name, string url, RemoteCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        if (_vault.GetKeyState() != KeyState.Ok)
        {
            return RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.Suspended);
        }

        if (string.IsNullOrWhiteSpace(name)
            || string.IsNullOrEmpty(credentials.Username)
            || !RemoteUrl.TryNormalize(url, out var normalizedUrl))
        {
            return RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.InvalidServer);
        }

        // Checked again in the change that adds it, where no other save can take the name meanwhile.
        if (_store.Read(config => NameHeldElsewhere(config, Guid.Empty, name)))
        {
            return RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.NameTaken);
        }

        var serverId = Guid.NewGuid();
        var endpoint = EndpointOf(serverId, normalizedUrl);

        var login = await LogInAsync(endpoint, credentials, name, cancellationToken).ConfigureAwait(false);
        if (!login.IsSuccess)
        {
            _logger.LogWarning("Login on new remote server {ServerName} failed: {Failure}", LogText.Clean(name), login.Failure);
            return RemoteResult.Fail<RemoteServerInfo>(login.Failure);
        }

        // The Server Id of the remote says whether it is configured already (120). A removed one comes back as new (408).
        var remoteId = login.Value!.ServerId;
        if (remoteId is not null && _store.Read(config => ConfiguredAs(config, remoteId)) is { } configured)
        {
            await EndSessionAsync(endpoint, login.Value, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Remote server {ServerName} is already configured as {ConfiguredName}", LogText.Clean(name), LogText.Clean(configured.Name));
            return RemoteResult.Fail(RemoteFailure.AlreadyConfigured, configured);
        }

        var capabilities = await DetectCapabilitiesAsync(endpoint, login.Value!, name, cancellationToken).ConfigureAwait(false);

        // As for new credentials (419): a login whose credentials cannot be saved is ended.
        var encrypted = _vault.Protect(serverId, credentials);
        if (encrypted is null)
        {
            await EndUnusedLoginAsync(endpoint, login.Value!, name, refusedIsEnded: false).ConfigureAwait(false);
            return RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.Suspended);
        }

        var server = new RemoteServerConfig
        {
            Id = serverId,
            Name = name.Trim(),
            Url = normalizedUrl,
            EncryptedCredentials = encrypted,
            Mode = capabilities.Mode,
            ProtocolVersion = capabilities.ProtocolVersion,
            JellyfinServerId = remoteId ?? string.Empty
        };

        // Checked again in the change that adds it: another add of the same remote may have ended meanwhile.
        RemoteServerInfo? standing = null;
        var added = false;
        var nameTaken = false;
        try
        {
            // A failed save takes the server out of memory under the same lock, so no call sees it (419).
            _store.Update(
                config =>
                {
                    standing = remoteId is null ? null : ConfiguredAs(config, remoteId);
                    nameTaken = standing is null && NameHeldElsewhere(config, serverId, server.Name);
                    if (standing is null && !nameTaken)
                    {
                        config.Servers.Add(server);
                        added = true;
                    }
                },
                config =>
                {
                    if (added)
                    {
                        config.Servers.Remove(server);
                    }
                });
        }
        catch (Exception exception)
        {
            await EndUnsavedAddAsync(server, endpoint, login.Value!, exception).ConfigureAwait(false);
            throw;
        }

        if (nameTaken)
        {
            await EndSessionAsync(endpoint, login.Value!, cancellationToken).ConfigureAwait(false);
            return RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.NameTaken);
        }

        if (standing is not null)
        {
            await EndSessionAsync(endpoint, login.Value!, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Remote server {ServerName} is already configured as {ConfiguredName}", LogText.Clean(name), LogText.Clean(standing.Name));
            return RemoteResult.Fail(RemoteFailure.AlreadyConfigured, standing);
        }

        // Under the login gate, not cancelled: the server is saved, so its login must be kept or ended (413).
        var runtime = RuntimeOf(serverId);
        await runtime.LoginGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // A call that logged in since the save holds the kept login. The remote revokes this one only for the same
            // user, so it is ended here, a refused token counting as ended.
            if (runtime.Session is not null)
            {
                await EndUnusedLoginAsync(endpoint, login.Value!, server.Name, refusedIsEnded: true).ConfigureAwait(false);
            }
            else if (!runtime.TryKeep(login.Value!) || !IsConfigured(serverId))
            {
                await EndLoginOfRemovedAsync(serverId, runtime, endpoint, login.Value!, server.Name, freshMayBeRevoked: true).ConfigureAwait(false);
                return RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.UnknownServer);
            }
        }
        finally
        {
            runtime.LoginGate.Release();
        }

        Record(runtime, server.Name, RemoteFailure.None, countsForActivity: true);

        _logger.LogInformation("Added remote server {ServerName} in {Mode} mode", LogText.Clean(server.Name), server.Mode);
        return RemoteResult.Success<RemoteServerInfo>(_store.Read(_ => ToInfo(server)));
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteServerInfo>> UpdateCredentialsAsync(Guid serverId, RemoteCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        if (_vault.GetKeyState() == KeyState.Missing)
        {
            return RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.Suspended);
        }

        if (string.IsNullOrEmpty(credentials.Username))
        {
            return RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.InvalidServer);
        }

        var stored = _store.Read(config => config.Servers.FirstOrDefault(server => server.Id == serverId) is { } server
            ? new StoredServer(server.Id, server.Name, server.Url)
            : null);
        if (stored is null)
        {
            return RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.UnknownServer);
        }

        // Under the login gate, as in GetSessionAsync: the remote keeps only the last login, so that must be the one kept (413).
        var endpoint = EndpointOf(serverId, stored.Url);
        var runtime = RuntimeOf(serverId);
        try
        {
            await runtime.LoginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DropIfRemoved(serverId, runtime);
            throw;
        }

        var kept = false;
        try
        {
            var login = await LogInAsync(endpoint, credentials, stored.Name, cancellationToken).ConfigureAwait(false);
            if (!login.IsSuccess)
            {
                _logger.LogWarning("Login on remote server {ServerName} with new credentials failed: {Failure}", LogText.Clean(stored.Name), login.Failure);
                return RemoteResult.Fail<RemoteServerInfo>(login.Failure);
            }

            if (RefusedServerId(serverId, login.Value!.ServerId) is { } refusedFor)
            {
                await RefuseOtherServerAsync(endpoint, login.Value!, stored.Name, refusedFor).ConfigureAwait(false);
                return RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.OtherServer);
            }

            // New credentials that cannot be saved end their login; the kept one stays and logs in again at its 401 (419).
            var encrypted = _vault.Protect(serverId, credentials);
            if (encrypted is null)
            {
                await EndUnusedLoginAsync(endpoint, login.Value!, stored.Name, refusedIsEnded: false).ConfigureAwait(false);
                return RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.Suspended);
            }

            var found = false;
            var heldElsewhere = false;
            string? previous = null;
            string? savedId = null;
            try
            {
                // A failed save puts the saved credentials, and no Server Id this change wrote, back under the same lock,
                // so no call or removal picks up the unsaved ones (419).
                _store.Update(
                    config =>
                    {
                        if (config.Servers.FirstOrDefault(server => server.Id == serverId) is { } server)
                        {
                            previous = server.EncryptedCredentials;
                            server.EncryptedCredentials = encrypted;
                            if (server.JellyfinServerId.Length == 0 && login.Value!.ServerId is { } remoteId)
                            {
                                heldElsewhere = !TryStoreServerId(config, server, remoteId);
                                savedId = heldElsewhere ? null : remoteId;
                            }

                            found = true;
                        }
                    },
                    config =>
                    {
                        if (previous is not null
                            && config.Servers.FirstOrDefault(server => server.Id == serverId) is { } server
                            && server.EncryptedCredentials == encrypted)
                        {
                            server.EncryptedCredentials = previous;
                            if (savedId is not null && server.JellyfinServerId == savedId)
                            {
                                server.JellyfinServerId = string.Empty;
                            }
                        }
                    });
            }
            catch
            {
                await EndUnusedLoginAsync(endpoint, login.Value!, stored.Name, refusedIsEnded: false).ConfigureAwait(false);
                throw;
            }

            if (!found)
            {
                await EndLoginOfRemovedAsync(serverId, runtime, endpoint, login.Value!, stored.Name).ConfigureAwait(false);
                return RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.UnknownServer);
            }

            if (heldElsewhere)
            {
                WarnServerIdHeldElsewhere(stored.Name, login.Value!.ServerId!);
            }

            // As in GetSessionAsync: a removal that ended meanwhile must not leave this login behind (413).
            if (!runtime.TryKeep(login.Value!) || !IsConfigured(serverId))
            {
                await EndLoginOfRemovedAsync(serverId, runtime, endpoint, login.Value!, stored.Name).ConfigureAwait(false);
                return RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.UnknownServer);
            }

            kept = true;
        }
        finally
        {
            if (!kept)
            {
                DropIfRemoved(serverId, runtime);
            }

            runtime.LoginGate.Release();
        }

        Record(runtime, stored.Name, RemoteFailure.None, countsForActivity: true);
        _logger.LogInformation("Updated the credentials of remote server {ServerName}", LogText.Clean(stored.Name));
        var info = _store.Read(config => config.Servers.FirstOrDefault(server => server.Id == serverId) is { } server ? ToInfo(server) : null);
        return info is null ? RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.UnknownServer) : RemoteResult.Success(info);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteEditReport>> CheckEditAsync(Guid serverId, RemoteServerEdit edit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var (plan, refusal) = PlanEdit(serverId, edit);
        return plan is null ? refusal : await CheckPlanAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    // Logs in with the edited data on a check device, accepts only the saved Server Id and reads what the user sees.
    private async Task<RemoteResult<RemoteEditReport>> CheckPlanAsync(EditPlan plan, CancellationToken cancellationToken)
    {
        if (await RefuseNewAddressAsync(plan, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var endpoint = CheckEndpointOf(plan.ServerId, plan.Url);
        var login = await LogInAsync(endpoint, plan.Credentials, plan.Name, cancellationToken).ConfigureAwait(false);
        if (login.Value is not { } session)
        {
            _logger.LogWarning("Login on remote server {ServerName} with the edited data failed: {Failure}", LogText.Clean(plan.Name), login.Failure);
            return OfflineOr(plan, login.Failure, new RemoteEditReport(FieldOf(login.Failure)));
        }

        try
        {
            var read = await ReadEditedAsync(plan, endpoint, session, cancellationToken).ConfigureAwait(false);
            return read.IsSuccess ? RemoteResult.Success(read.Value!.Report) : OfflineOr(plan, read.Failure, read.Value!.Report);
        }
        finally
        {
            // Not cancelled with the request: the login must not stay open on the remote (419).
            await EndUnusedLoginAsync(endpoint, session, plan.Name, refusedIsEnded: true).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteEditReport>> SaveEditAsync(Guid serverId, RemoteServerEdit edit, Func<Task>? stopStreams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var (plan, refusal) = PlanEdit(serverId, edit);
        if (plan is null)
        {
            return refusal;
        }

        // Only a new name needs no remote (D28): it goes through the same check, so that both give the same outcome.
        if ((plan.Changes & RemoteChanges) == RemoteEditChanges.None)
        {
            var checkedEdit = await CheckPlanAsync(plan, cancellationToken).ConfigureAwait(false);
            if (!checkedEdit.IsSuccess)
            {
                return checkedEdit;
            }

            if (StoreEdit(plan, null, null, null) is var unstored and not RemoteFailure.None)
            {
                return Refuse(unstored, FieldOf(unstored)).Refusal;
            }

            _logger.LogInformation("Saved the edit of remote server {ServerName}", LogText.Clean(plan.Name));
            var report = checkedEdit.Value!;
            return RemoteResult.Success(new RemoteEditReport(RemoteEditField.None)
            {
                Changes = plan.Changes,
                LibraryIds = plan.Saved.LibraryIds,
                RemoteOffline = report.RemoteOffline,
                RemoteWarning = report.RemoteWarning
            });
        }

        if (await RefuseNewAddressAsync(plan, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        if ((plan.Changes & (RemoteEditChanges.User | RemoteEditChanges.Password)) == RemoteEditChanges.None)
        {
            return await SaveEditWithCheckLoginAsync(plan, cancellationToken).ConfigureAwait(false);
        }

        var (saved, replaced) = await SaveEditWithNewLoginAsync(plan, cancellationToken).ConfigureAwait(false);
        if (saved.IsSuccess && plan.Changes.HasFlag(RemoteEditChanges.User))
        {
            await EndOldUserAsync(plan, replaced, stopStreams).ConfigureAwait(false);
        }

        return saved;
    }

    /// <inheritdoc />
    public async Task<IDisposable?> EnterEditAsync(Guid serverId, CancellationToken cancellationToken)
    {
        if (IsRemoving(serverId))
        {
            return null;
        }

        var gate = _editGates.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (IsRemoving(serverId))
        {
            gate.Release();
            return null;
        }

        return new Release(() => gate.Release());
    }

    /// <inheritdoc />
    public async Task<IDisposable> BeginRemovalAsync(Guid serverId, CancellationToken cancellationToken)
    {
        lock (_removingLock)
        {
            _removing[serverId] = _removing.GetValueOrDefault(serverId) + 1;
        }

        var unmark = new Release(() =>
        {
            lock (_removingLock)
            {
                if (_removing.GetValueOrDefault(serverId) <= 1)
                {
                    _removing.Remove(serverId);
                }
                else
                {
                    _removing[serverId]--;
                }
            }
        });

        var gate = _editGates.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            unmark.Dispose();
            throw;
        }

        return new Release(() =>
        {
            gate.Release();
            unmark.Dispose();
        });
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(Guid serverId)
    {
        RemoteServerConfig? removed = null;
        var position = -1;

        // A failed save puts the server back in memory under the same lock: the file still has it.
        _store.Update(
            config =>
            {
                removed = config.Servers.FirstOrDefault(candidate => candidate.Id == serverId);
                if (removed is not null)
                {
                    position = config.Servers.IndexOf(removed);
                    config.Servers.RemoveAt(position);
                    HoldLibraries(config, removed);
                }
            },
            config =>
            {
                if (removed is not null && position >= 0 && !config.Servers.Contains(removed))
                {
                    config.ReleaseHeldAutomaticLibraries(removed.Id, removed.AutomaticLibraryRegistry);
                    config.Servers.Insert(Math.Min(position, config.Servers.Count), removed);
                }
            });

        if (removed is null)
        {
            return false;
        }

        // Not disposed here: a call may still hold its login gate. The registry disposes the rest.
        var session = _runtimes.TryRemove(serverId, out var runtime) ? runtime.Forget() : null;
        if (session is null)
        {
            await EndDeviceWithoutLiveLoginAsync(removed, runtime).ConfigureAwait(false);
        }
        else
        {
            await EndRemovedSessionAsync(removed, session).ConfigureAwait(false);
        }

        return true;
    }

    /// <inheritdoc />
    public bool Rename(Guid serverId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // False also for a name another server holds, checked in the same change that stores it.
        var renamed = false;
        _store.Update(config =>
        {
            if (config.Servers.FirstOrDefault(candidate => candidate.Id == serverId) is { } server && !NameHeldElsewhere(config, serverId, name))
            {
                renamed = true;
                server.Name = name.Trim();
            }
        });

        return renamed;
    }

    /// <inheritdoc />
    public async Task<RemoteResult<ConnectionReport>> TestAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var result = await RunWithSessionAsync(
            serverId,
            async (endpoint, session, token) =>
            {
                // Capabilities first: they decide where the libraries are read from.
                var name = _store.Read(config => config.Servers.FirstOrDefault(server => server.Id == serverId)?.Name) ?? string.Empty;
                var capabilities = await DetectCapabilitiesAsync(endpoint, session, name, token).ConfigureAwait(false);
                var libraries = await ReadLibrariesAsync(capabilities.Mode, endpoint, session, token).ConfigureAwait(false);
                return libraries.IsSuccess
                    ? RemoteResult.Success<ConnectionReport>(new ConnectionReport(capabilities, libraries.Value!.Count))
                    : RemoteResult.Fail<ConnectionReport>(libraries.Failure);
            },
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            var capabilities = result.Value!.Capabilities;
            _store.Update(config =>
            {
                var server = config.Servers.FirstOrDefault(candidate => candidate.Id == serverId);
                if (server is not null)
                {
                    server.Mode = capabilities.Mode;
                    server.ProtocolVersion = capabilities.ProtocolVersion;
                }
            });
        }

        return result;
    }

    /// <inheritdoc />
    public Task<RemoteResult<IReadOnlyList<RemoteLibrary>>> GetLibrariesAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var mode = _store.Read(config => config.Servers.FirstOrDefault(server => server.Id == serverId)?.Mode) ?? RemoteMode.Unknown;
        return RunWithSessionAsync(serverId, (endpoint, session, token) => ReadLibrariesAsync(mode, endpoint, session, token), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RemoteServerInfo?> RefreshCapabilitiesAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var detected = await RunWithSessionAsync(
            serverId,
            (endpoint, session, token) => _client.DetectCapabilitiesAsync(endpoint, session, token),
            cancellationToken).ConfigureAwait(false);

        var known = GetServers().FirstOrDefault(server => server.Id == serverId);
        if (!detected.IsSuccess || known is null || (known.Mode == detected.Value!.Mode && known.ProtocolVersion == detected.Value.ProtocolVersion))
        {
            return known;
        }

        _store.Update(config =>
        {
            var server = config.Servers.FirstOrDefault(candidate => candidate.Id == serverId);
            if (server is not null)
            {
                server.Mode = detected.Value.Mode;
                server.ProtocolVersion = detected.Value.ProtocolVersion;
            }
        });

        _logger.LogInformation("Remote server {ServerName} is now in {Mode} mode", LogText.Clean(known.Name), detected.Value.Mode);
        return GetServers().FirstOrDefault(server => server.Id == serverId);
    }

    /// <inheritdoc />
    public bool SetImportedLibraries(Guid serverId, IEnumerable<string> libraryIds)
    {
        var ids = libraryIds.Distinct(StringComparer.Ordinal).ToList();
        var found = false;
        _store.Update(config =>
        {
            var server = config.Servers.FirstOrDefault(candidate => candidate.Id == serverId);
            if (server is null)
            {
                return;
            }

            found = true;
            server.ImportedLibraryIds.Clear();
            foreach (var id in ids)
            {
                server.ImportedLibraryIds.Add(id);
            }
        });

        return found;
    }

    /// <inheritdoc />
    public Task<RemoteResult<T>> RunWithSessionAsync<T>(
        Guid serverId,
        Func<RemoteEndpoint, RemoteSession, CancellationToken, Task<RemoteResult<T>>> call,
        CancellationToken cancellationToken)
        => RunCallAsync(serverId, call, countsForActivity: true, cancellationToken);

    /// <inheritdoc />
    public Task<RemoteResult<T>> RunPlaybackCallAsync<T>(
        Guid serverId,
        Func<RemoteEndpoint, RemoteSession, CancellationToken, Task<RemoteResult<T>>> call,
        CancellationToken cancellationToken)
        => RunCallAsync(serverId, call, countsForActivity: false, cancellationToken);

    // Runs a call with a session; only sync and admin calls tell the activity log the server went offline or came back (130).
    private async Task<RemoteResult<T>> RunCallAsync<T>(
        Guid serverId,
        Func<RemoteEndpoint, RemoteSession, CancellationToken, Task<RemoteResult<T>>> call,
        bool countsForActivity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (_vault.GetKeyState() != KeyState.Ok)
        {
            return RemoteResult.Fail<T>(RemoteFailure.Suspended);
        }

        var server = _store.Read(config => config.Servers
            .Where(candidate => candidate.Id == serverId)
            .Select(candidate => new StoredServer(candidate.Id, candidate.Name, candidate.Url))
            .FirstOrDefault());
        if (server is null)
        {
            return RemoteResult.Fail<T>(RemoteFailure.UnknownServer);
        }

        var runtime = RuntimeOf(serverId);
        var endpoint = EndpointOf(serverId, server.Url);

        var result = await CallOnceAsync(server, endpoint, runtime, null, call, cancellationToken).ConfigureAwait(false);
        if (result.Attempt.Failure == RemoteFailure.AuthenticationFailed && result.Session is not null)
        {
            result = await CallOnceAsync(server, endpoint, runtime, result.Session, call, cancellationToken).ConfigureAwait(false);
        }

        Record(runtime, server.Name, result.Attempt.Failure, countsForActivity);
        if (!result.Attempt.IsSuccess)
        {
            _logger.LogWarning("Call to remote server {ServerName} failed: {Failure}", LogText.Clean(server.Name), result.Attempt.Failure);
        }

        return result.Attempt;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var runtime in _runtimes.Values)
        {
            runtime.Dispose();
        }

        foreach (var gate in _editGates.Values)
        {
            gate.Dispose();
        }
    }

    // The libraries of the server are deleted after it, including those a switch-on still running adds (140).
    private static void HoldLibraries(PluginConfiguration config, RemoteServerConfig removed)
    {
        if (removed.AutomaticLibraries || removed.AutomaticLibraryRegistry.Count > 0)
        {
            config.HoldAutomaticLibraries(removed.Id, removed.AutomaticLibraryRegistry, removed.PreexistingLibraryIds, removed.FollowedLibraryIds, removed.FollowSkipped);
        }
    }

    private static RemoteEndpoint EndpointOf(Guid serverId, string url)
    {
        return new RemoteEndpoint(new Uri(url + "/"), $"jellymesh-{serverId:N}");
    }

    // The device of the logins that check an edit: a login on the kept device with the same user would revoke the kept one.
    private static RemoteEndpoint CheckEndpointOf(Guid serverId, string url)
    {
        return new RemoteEndpoint(new Uri(url + "/"), $"jellymesh-{serverId:N}-check");
    }

    // The field of the form a failed login is about.
    private static RemoteEditField FieldOf(RemoteFailure failure) => failure switch
    {
        RemoteFailure.AuthenticationFailed => RemoteEditField.Password,
        RemoteFailure.AdministratorAccount or RemoteFailure.Forbidden => RemoteEditField.Username,
        RemoteFailure.Suspended or RemoteFailure.UnknownServer => RemoteEditField.None,
        RemoteFailure.NameTaken => RemoteEditField.Name,
        _ => RemoteEditField.Url
    };

    private static (EditPlan? Plan, RemoteResult<RemoteEditReport> Refusal) Refuse(RemoteFailure failure, RemoteEditField field)
        => (null, RemoteResult.Fail(failure, new RemoteEditReport(field)));

    // A remote that cannot be read blocks only an edit that needs it; otherwise the edit goes on with the reason as a
    // warning, offline only when the remote did not answer. Another server at the address always blocks (D28).
    private static RemoteResult<RemoteEditReport> OfflineOr(EditPlan plan, RemoteFailure failure, RemoteEditReport refusal)
    {
        return failure is not (RemoteFailure.Suspended or RemoteFailure.UnknownServer or RemoteFailure.OtherServer) && (plan.Changes & RemoteChanges) == RemoteEditChanges.None
            ? RemoteResult.Success(new RemoteEditReport(RemoteEditField.None)
            {
                Changes = plan.Changes,
                LibraryIds = plan.Saved.LibraryIds,
                RemoteOffline = failure is RemoteFailure.Unreachable or RemoteFailure.Timeout,
                RemoteWarning = failure
            })
            : RemoteResult.Fail(failure, refusal);
    }

    // Checks the fields of an edit against the saved server and finds what it changes, or why it is refused (row 432).
    private (EditPlan? Plan, RemoteResult<RemoteEditReport> Refusal) PlanEdit(Guid serverId, RemoteServerEdit edit)
    {
        if (_vault.GetKeyState() == KeyState.Missing)
        {
            return Refuse(RemoteFailure.Suspended, RemoteEditField.None);
        }

        if (string.IsNullOrWhiteSpace(edit.Name))
        {
            return Refuse(RemoteFailure.InvalidServer, RemoteEditField.Name);
        }

        if (!RemoteUrl.TryNormalize(edit.Url, out var url))
        {
            return Refuse(RemoteFailure.InvalidServer, RemoteEditField.Url);
        }

        if (string.IsNullOrEmpty(edit.Username))
        {
            return Refuse(RemoteFailure.InvalidServer, RemoteEditField.Username);
        }

        if (edit.LibraryMode is { } mode && !Enum.IsDefined(mode))
        {
            return Refuse(RemoteFailure.InvalidServer, RemoteEditField.LibraryMode);
        }

        var row = _store.Read(config => config.Servers.FirstOrDefault(server => server.Id == serverId) is { } server
            ? (Saved: new SavedServer(server.Name, server.Url, server.JellyfinServerId, server.ImportedLibraryIds.ToList(), server.LibraryMode), Encrypted: server.EncryptedCredentials)
            : default);
        if (row.Saved is not { } saved)
        {
            return Refuse(RemoteFailure.UnknownServer, RemoteEditField.None);
        }

        // Credentials that no longer decrypt are entered again here, so the password is then required (row 434).
        var stored = UnprotectOrNull(serverId, row.Encrypted);
        var keepsPassword = string.IsNullOrEmpty(edit.Password);
        if (keepsPassword && stored is null)
        {
            return Refuse(RemoteFailure.InvalidServer, RemoteEditField.Password);
        }

        var name = edit.Name.Trim();
        var changes = RemoteEditChanges.None;
        changes |= name == saved.Name ? RemoteEditChanges.None : RemoteEditChanges.Name;
        changes |= url == saved.Url ? RemoteEditChanges.None : RemoteEditChanges.Url;
        changes |= stored is not null && stored.Username == edit.Username ? RemoteEditChanges.None : RemoteEditChanges.User;
        changes |= keepsPassword || stored?.Password == edit.Password ? RemoteEditChanges.None : RemoteEditChanges.Password;
        var requested = edit.LibraryIds?.Distinct(StringComparer.Ordinal).ToList();
        changes |= requested is null || SameLibraries(requested, saved.LibraryIds) ? RemoteEditChanges.None : RemoteEditChanges.Libraries;
        var libraryMode = edit.LibraryMode ?? saved.LibraryMode;
        changes |= libraryMode == saved.LibraryMode ? RemoteEditChanges.None : RemoteEditChanges.LibraryMode;

        var credentials = new RemoteCredentials { Username = edit.Username, Password = keepsPassword ? stored!.Password : edit.Password! };
        return (new EditPlan(serverId, name, url, credentials, changes, saved, requested) { Stored = stored, LibraryMode = libraryMode }, RemoteResult.Fail<RemoteEditReport>(RemoteFailure.None));
    }

    // Saves an edit whose user and password stay with a login on the check device, so the kept login goes on (419).
    private async Task<RemoteResult<RemoteEditReport>> SaveEditWithCheckLoginAsync(EditPlan plan, CancellationToken cancellationToken)
    {
        var endpoint = CheckEndpointOf(plan.ServerId, plan.Url);
        var login = await LogInAsync(endpoint, plan.Credentials, plan.Name, cancellationToken).ConfigureAwait(false);
        if (login.Value is not { } session)
        {
            _logger.LogWarning("Login on remote server {ServerName} with the edited data failed: {Failure}", LogText.Clean(plan.Name), login.Failure);
            return RemoteResult.Fail(login.Failure, new RemoteEditReport(FieldOf(login.Failure)));
        }

        try
        {
            var read = await ReadEditedAsync(plan, endpoint, session, cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess)
            {
                return RemoteResult.Fail(read.Failure, read.Value!.Report);
            }

            if (StoreEdit(plan, null, read.Value!, session.ServerId) is var unstored and not RemoteFailure.None)
            {
                return Refuse(unstored, FieldOf(unstored)).Refusal;
            }

            Record(RuntimeOf(plan.ServerId), plan.Name, RemoteFailure.None, countsForActivity: true);
            _logger.LogInformation("Saved the edit of remote server {ServerName}", LogText.Clean(plan.Name));
            return RemoteResult.Success(read.Value!.Report);
        }
        finally
        {
            await EndUnusedLoginAsync(endpoint, session, plan.Name, refusedIsEnded: true).ConfigureAwait(false);
        }
    }

    // Saves an edit with a new user or password: logs in on the kept device under the login gate, as for new credentials,
    // and keeps that login only once the edit is saved; a refused or unsaved edit ends it (413, 419).
    // Returns the login it replaced, if any.
    private async Task<(RemoteResult<RemoteEditReport> Result, RemoteSession? Replaced)> SaveEditWithNewLoginAsync(EditPlan plan, CancellationToken cancellationToken)
    {
        var serverId = plan.ServerId;
        var endpoint = EndpointOf(serverId, plan.Url);
        var runtime = RuntimeOf(serverId);
        try
        {
            await runtime.LoginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DropIfRemoved(serverId, runtime);
            throw;
        }

        var kept = false;
        RemoteEditReport report;
        RemoteSession? replaced;
        try
        {
            var login = await LogInAsync(endpoint, plan.Credentials, plan.Name, cancellationToken).ConfigureAwait(false);
            if (login.Value is not { } session)
            {
                _logger.LogWarning("Login on remote server {ServerName} with the edited data failed: {Failure}", LogText.Clean(plan.Name), login.Failure);
                return (RemoteResult.Fail(login.Failure, new RemoteEditReport(FieldOf(login.Failure))), null);
            }

            RemoteResult<EditRead> read;
            try
            {
                read = await ReadEditedAsync(plan, endpoint, session, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await EndUnusedLoginAsync(endpoint, session, plan.Name, refusedIsEnded: false).ConfigureAwait(false);
                throw;
            }

            if (!read.IsSuccess)
            {
                await EndUnusedLoginAsync(endpoint, session, plan.Name, refusedIsEnded: false).ConfigureAwait(false);
                return (RemoteResult.Fail(read.Failure, read.Value!.Report), null);
            }

            // Credentials that cannot be encrypted are not saved, so their login ends (419).
            var encrypted = _vault.Protect(serverId, plan.Credentials);
            if (encrypted is null)
            {
                await EndUnusedLoginAsync(endpoint, session, plan.Name, refusedIsEnded: false).ConfigureAwait(false);
                return (Refuse(RemoteFailure.Suspended, RemoteEditField.None).Refusal, null);
            }

            RemoteFailure unstored;
            try
            {
                unstored = StoreEdit(plan, encrypted, read.Value!, session.ServerId);
            }
            catch
            {
                await EndUnusedLoginAsync(endpoint, session, plan.Name, refusedIsEnded: false).ConfigureAwait(false);
                throw;
            }

            if (unstored == RemoteFailure.NameTaken)
            {
                await EndUnusedLoginAsync(endpoint, session, plan.Name, refusedIsEnded: false).ConfigureAwait(false);
                return (Refuse(unstored, RemoteEditField.Name).Refusal, null);
            }

            var found = unstored == RemoteFailure.None;

            // As in GetSessionAsync: a removal that ended meanwhile must not leave this login behind (413).
            replaced = runtime.Session;
            if (!found || !runtime.TryKeep(session) || !IsConfigured(serverId))
            {
                await EndLoginOfRemovedAsync(serverId, runtime, endpoint, session, plan.Name).ConfigureAwait(false);
                return (Refuse(RemoteFailure.UnknownServer, RemoteEditField.None).Refusal, null);
            }

            kept = true;
            report = read.Value!.Report;
        }
        finally
        {
            if (!kept)
            {
                DropIfRemoved(serverId, runtime);
            }

            runtime.LoginGate.Release();
        }

        Record(runtime, plan.Name, RemoteFailure.None, countsForActivity: true);
        _logger.LogInformation("Saved the edit of remote server {ServerName}", LogText.Clean(plan.Name));
        return (RemoteResult.Success(report), replaced);
    }

    // After a new user is saved: stops the streams of the server, then ends the login of the old user within the logout
    // limit, the live one or, without it, one made with the old credentials on the same device (row 434, 413).
    private async Task EndOldUserAsync(EditPlan plan, RemoteSession? replaced, Func<Task>? stopStreams)
    {
        // Together, so that both end within one limit (row 434).
        var name = LogText.Clean(plan.Name);
        await Task.WhenAll(StopStreamsAsync(name, stopStreams), LogOutOldUserAsync(plan, name, replaced)).ConfigureAwait(false);
    }

    // Never throws: the edit is saved, so it must not fail now.
    private async Task StopStreamsAsync(string? name, Func<Task>? stopStreams)
    {
        if (stopStreams is null)
        {
            return;
        }

        try
        {
            await stopStreams().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The type only: a message could hold an address.
            _logger.LogWarning("Could not stop the streams of remote server {ServerName} after its user changed: {Error}", name, exception.GetType().Name);
        }
    }

    // Ends the live login of the old user, or logs it in and out on the kept device; never throws.
    private async Task LogOutOldUserAsync(EditPlan plan, string? name, RemoteSession? replaced)
    {
        // A new address is the same server only when the saved Server Id was checked there; else the old user stays where it was.
        var endpoint = EndpointOf(plan.ServerId, plan.Saved.JellyfinServerId.Length > 0 ? plan.Url : plan.Saved.Url);
        using var limit = new CancellationTokenSource(LogoutLimit);
        RemoteFailure failure;
        try
        {
            if (replaced is not null)
            {
                failure = await LogOutUntilAsync(endpoint, replaced, limit).ConfigureAwait(false);
            }
            else if (plan.Stored is { } old && old.Username != plan.Credentials.Username)
            {
                var login = await _client.AuthenticateAsync(endpoint, old, limit.Token).ConfigureAwait(false);
                failure = login.Value is { } session ? await LogOutUntilAsync(endpoint, session, limit).ConfigureAwait(false) : login.Failure;
            }
            else
            {
                return;
            }
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            failure = RemoteFailure.Timeout;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold an address.
            _logger.LogWarning("Could not end the login of the old user on remote server {ServerName}: {Error}; its JellyMesh device may stay listed there", name, exception.GetType().Name);
            return;
        }

        // A refused token was revoked already, as when the user is in fact the same.
        if (failure is RemoteFailure.None or RemoteFailure.AuthenticationFailed)
        {
            _logger.LogInformation("Ended the login of the old user on remote server {ServerName}", name);
        }
        else
        {
            _logger.LogWarning("Could not end the login of the old user on remote server {ServerName}: {Failure}; its JellyMesh device may stay listed there", name, failure);
        }
    }

    // Writes an edit in one save; a failed save puts every field back under the same lock (row 432). Says why nothing was
    // written: the server is gone, or another server took the new name meanwhile.
    private RemoteFailure StoreEdit(EditPlan plan, string? encrypted, EditRead? read, string? remoteServerId)
    {
        var found = false;
        var nameTaken = false;
        var heldElsewhere = false;
        RemoteServerConfig? before = null;
        string? savedId = null;
        _store.Update(
            config =>
            {
                if (config.Servers.FirstOrDefault(server => server.Id == plan.ServerId) is not { } server)
                {
                    return;
                }

                found = true;
                nameTaken = (plan.Changes & RemoteEditChanges.Name) != RemoteEditChanges.None && NameHeldElsewhere(config, plan.ServerId, plan.Name);
                if (nameTaken)
                {
                    return;
                }

                before = new RemoteServerConfig
                {
                    Name = server.Name,
                    Url = server.Url,
                    EncryptedCredentials = server.EncryptedCredentials,
                    Mode = server.Mode,
                    ProtocolVersion = server.ProtocolVersion,
                    LibraryMode = server.LibraryMode
                };
                foreach (var id in server.ImportedLibraryIds)
                {
                    before.ImportedLibraryIds.Add(id);
                }

                server.Name = plan.Name;
                server.Url = plan.Url;
                server.EncryptedCredentials = encrypted ?? server.EncryptedCredentials;
                if (plan.Changes.HasFlag(RemoteEditChanges.LibraryMode))
                {
                    server.LibraryMode = plan.LibraryMode;
                }

                if (read is not null)
                {
                    server.ImportedLibraryIds.Clear();
                    foreach (var id in read.Report.LibraryIds)
                    {
                        server.ImportedLibraryIds.Add(id);
                    }

                    server.Mode = read.Capabilities!.Mode;
                    server.ProtocolVersion = read.Capabilities.ProtocolVersion;
                }

                if (server.JellyfinServerId.Length == 0 && remoteServerId is not null)
                {
                    heldElsewhere = !TryStoreServerId(config, server, remoteServerId);
                    savedId = heldElsewhere ? null : remoteServerId;
                }
            },
            config =>
            {
                if (before is null || config.Servers.FirstOrDefault(server => server.Id == plan.ServerId) is not { } server)
                {
                    return;
                }

                server.Name = before.Name;
                server.Url = before.Url;
                server.EncryptedCredentials = before.EncryptedCredentials;
                server.Mode = before.Mode;
                server.ProtocolVersion = before.ProtocolVersion;
                server.LibraryMode = before.LibraryMode;
                server.ImportedLibraryIds.Clear();
                foreach (var id in before.ImportedLibraryIds)
                {
                    server.ImportedLibraryIds.Add(id);
                }

                if (savedId is not null && server.JellyfinServerId == savedId)
                {
                    server.JellyfinServerId = string.Empty;
                }
            });

        if (heldElsewhere)
        {
            WarnServerIdHeldElsewhere(plan.Name, remoteServerId!);
        }

        return nameTaken ? RemoteFailure.NameTaken : found ? RemoteFailure.None : RemoteFailure.UnknownServer;
    }

    // Reads, with no credentials, the Server Id at a new address, so that the password goes only to the saved server (row 433).
    private async Task<RemoteResult<RemoteEditReport>?> RefuseNewAddressAsync(EditPlan plan, CancellationToken cancellationToken)
    {
        var savedId = plan.Saved.JellyfinServerId;
        if ((plan.Changes & RemoteEditChanges.Url) == RemoteEditChanges.None || savedId.Length == 0)
        {
            return null;
        }

        var info = await _client.GetPublicInfoAsync(CheckEndpointOf(plan.ServerId, plan.Url), null, cancellationToken).ConfigureAwait(false);
        if (info.Value is not { } answered)
        {
            return RemoteResult.Fail(info.Failure, new RemoteEditReport(RemoteEditField.Url));
        }

        if (answered.ServerId == savedId)
        {
            return null;
        }

        WarnOtherServer(plan.Saved.Name, answered.ServerId, savedId);
        return RemoteResult.Fail(RemoteFailure.OtherServer, new RemoteEditReport(RemoteEditField.Url) { ServerName = answered.ServerName });
    }

    // Logs in, and ends at once a login the remote opened but that is refused (419).
    private async Task<RemoteResult<RemoteSession>> LogInAsync(RemoteEndpoint endpoint, RemoteCredentials credentials, string serverName, CancellationToken cancellationToken)
    {
        var login = await _client.AuthenticateAsync(endpoint, credentials, cancellationToken).ConfigureAwait(false);
        if (login.IsSuccess || login.Value is not { } refused)
        {
            return login;
        }

        await EndUnusedLoginAsync(endpoint, refused, serverName, refusedIsEnded: true).ConfigureAwait(false);
        return RemoteResult.Fail<RemoteSession>(login.Failure);
    }

    private static bool SameLibraries(IReadOnlyCollection<string> first, IReadOnlyCollection<string> second)
        => first.Count == second.Count && first.All(second.Contains);

    // Accepts only the saved Server Id (row 433), then reads what the remote offers the user with the edited data.
    private async Task<RemoteResult<EditRead>> ReadEditedAsync(EditPlan plan, RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        var savedId = plan.Saved.JellyfinServerId;
        if (savedId.Length > 0 && session.ServerId != savedId)
        {
            WarnOtherServer(plan.Saved.Name, session.ServerId, savedId);
            var answered = await _client.GetPublicInfoAsync(endpoint, session, cancellationToken).ConfigureAwait(false);
            return RemoteResult.Fail(RemoteFailure.OtherServer, new EditRead(new RemoteEditReport(RemoteEditField.Url) { ServerName = answered.Value?.ServerName }, null));
        }

        // As for an add (120): another configured server already stands for this remote.
        if (savedId.Length == 0 && session.ServerId is { } readId
            && _store.Read(config => config.Servers.FirstOrDefault(server => server.Id != plan.ServerId && server.JellyfinServerId == readId)?.Name) is { } configured)
        {
            return RemoteResult.Fail(RemoteFailure.AlreadyConfigured, new EditRead(new RemoteEditReport(RemoteEditField.Url) { ServerName = configured }, null));
        }

        var capabilities = await DetectCapabilitiesAsync(endpoint, session, plan.Name, cancellationToken).ConfigureAwait(false);
        var libraries = await ReadLibrariesAsync(capabilities.Mode, endpoint, session, cancellationToken).ConfigureAwait(false);
        if (libraries.Value is not { } visible)
        {
            return RemoteResult.Fail(libraries.Failure, new EditRead(new RemoteEditReport(RemoteEditField.LibraryIds), null));
        }

        // Libraries the user no longer sees leave the imported ones (row 434).
        var ids = (plan.RequestedLibraryIds ?? plan.Saved.LibraryIds).Where(id => visible.Any(library => library.Id == id)).ToList();
        var changes = SameLibraries(ids, plan.Saved.LibraryIds) ? plan.Changes : plan.Changes | RemoteEditChanges.Libraries;
        var info = await _client.GetPublicInfoAsync(endpoint, session, cancellationToken).ConfigureAwait(false);
        var report = new RemoteEditReport(RemoteEditField.None) { Changes = changes, ServerName = info.Value?.ServerName, Libraries = visible, LibraryIds = ids };
        return RemoteResult.Success(new EditRead(report, capabilities));
    }

    private Task<RemoteResult<IReadOnlyList<RemoteLibrary>>> ReadLibrariesAsync(RemoteMode mode, RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        return mode == RemoteMode.JellyMesh
            ? _client.GetSharedLibrariesAsync(endpoint, session, cancellationToken)
            : _client.GetLibrariesAsync(endpoint, session, cancellationToken);
    }

    private async Task<SessionAttempt<T>> CallOnceAsync<T>(
        StoredServer server,
        RemoteEndpoint endpoint,
        ServerRuntime runtime,
        RemoteSession? refused,
        Func<RemoteEndpoint, RemoteSession, CancellationToken, Task<RemoteResult<T>>> call,
        CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(server, endpoint, runtime, refused, cancellationToken).ConfigureAwait(false);
        if (!session.IsSuccess)
        {
            return new SessionAttempt<T>(RemoteResult.Fail<T>(session.Failure), null);
        }

        var attempt = await call(endpoint, session.Value!, cancellationToken).ConfigureAwait(false);
        return new SessionAttempt<T>(attempt, session.Value);
    }

    private async Task<RemoteResult<RemoteSession>> GetSessionAsync(
        StoredServer server,
        RemoteEndpoint endpoint,
        ServerRuntime runtime,
        RemoteSession? refused,
        CancellationToken cancellationToken)
    {
        // One login at a time per server: a second one would log the first out on the remote.
        await runtime.LoginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (runtime.Session is { } current && !ReferenceEquals(current, refused))
            {
                return RemoteResult.Success<RemoteSession>(current);
            }

            // Read again under the gate: the copy taken before it may be stale.
            var encrypted = _store.Read(config => config.Servers.FirstOrDefault(candidate => candidate.Id == server.Id)?.EncryptedCredentials);
            if (encrypted is null)
            {
                DropIfRemoved(server.Id, runtime);
                return RemoteResult.Fail<RemoteSession>(RemoteFailure.UnknownServer);
            }

            var credentials = UnprotectOrNull(server.Id, encrypted);
            if (credentials is null)
            {
                DropIfRemoved(server.Id, runtime);
                return RemoteResult.Fail<RemoteSession>(RemoteFailure.Suspended);
            }

            var login = await LogInAsync(endpoint, credentials, server.Name, cancellationToken).ConfigureAwait(false);
            if (login.Value is not { } fresh)
            {
                runtime.Session = null;
                DropIfRemoved(server.Id, runtime);
                return login;
            }

            if (RefusedServerId(server.Id, fresh.ServerId) is { } refusedFor)
            {
                runtime.Session = null;
                await RefuseOtherServerAsync(endpoint, fresh, server.Name, refusedFor).ConfigureAwait(false);
                DropIfRemoved(server.Id, runtime);
                return RemoteResult.Fail<RemoteSession>(RemoteFailure.OtherServer);
            }

            // A login that ends after the removal of its server is ended at once, so that no device stays behind (413).
            if (!runtime.TryKeep(fresh) || !IsConfigured(server.Id))
            {
                await EndLoginOfRemovedAsync(server.Id, runtime, endpoint, fresh, server.Name).ConfigureAwait(false);
                return RemoteResult.Fail<RemoteSession>(RemoteFailure.UnknownServer);
            }

            if (fresh.ServerId is { } remoteId)
            {
                RememberServerId(server, remoteId);
            }

            return login;
        }
        finally
        {
            runtime.LoginGate.Release();
        }
    }

    private async Task<RemoteCapabilities> DetectCapabilitiesAsync(RemoteEndpoint endpoint, RemoteSession session, string serverName, CancellationToken cancellationToken)
    {
        var result = await _client.DetectCapabilitiesAsync(endpoint, session, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return result.Value!;
        }

        _logger.LogWarning("Capability detection on remote server {ServerName} failed: {Failure}", LogText.Clean(serverName), result.Failure);
        return new RemoteCapabilities(RemoteMode.Unknown, 0);
    }

    // The configured server that stands for a remote with this Server Id, if any (decision 120).
    // Tells whether a server other than this one has the name, trimmed and in any case.
    private static bool NameHeldElsewhere(PluginConfiguration config, Guid serverId, string name)
        => config.Servers.Any(server => server.Id != serverId && string.Equals(server.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    private RemoteServerInfo? ConfiguredAs(PluginConfiguration config, string remoteId) =>
        config.Servers.FirstOrDefault(server => server.JellyfinServerId == remoteId) is { } configured ? ToInfo(configured) : null;

    // Ends a login that will not be used, so that its device does not stay listed on the remote.
    private async Task EndSessionAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        var result = await _client.LogoutAsync(endpoint, session, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.LogInformation("Could not end an unused login on a remote server: {Failure}", result.Failure);
        }
    }

    // Ends the live login of a removed server within the logout limit. On a failure its device stays (413).
    private async Task EndRemovedSessionAsync(RemoteServerConfig removed, RemoteSession session)
    {
        var name = LogText.Clean(removed.Name);
        var failure = await LogOutWithinLimitAsync(EndpointOf(removed.Id, removed.Url), session).ConfigureAwait(false);
        if (failure == RemoteFailure.None)
        {
            _logger.LogInformation("Ended the login on removed remote server {ServerName}", name);
        }
        else
        {
            // A logout cut by the limit may still reach the remote later.
            _logger.LogWarning("Could not end the login on removed remote server {ServerName}: {Failure}; its JellyMesh device may stay listed there", name, failure);
        }
    }

    // Without a live login, as after a restart, logs in with the same device and out at once, all within the logout limit
    // and after any login of the forgotten runtime (418). The login is kept nowhere; on a failure the device stays.
    private async Task EndDeviceWithoutLiveLoginAsync(RemoteServerConfig removed, ServerRuntime? runtime)
    {
        var name = LogText.Clean(removed.Name);
        using var limit = new CancellationTokenSource(LogoutLimit);
        var held = false;
        RemoteFailure failure;
        try
        {
            if (UnprotectOrNull(removed.Id, removed.EncryptedCredentials) is not { } credentials)
            {
                _logger.LogWarning("Removed remote server {ServerName} had no live login and its stored credentials cannot be read; its JellyMesh device may stay listed there", name);
                return;
            }

            var endpoint = EndpointOf(removed.Id, removed.Url);
            if (runtime is not null)
            {
                await runtime.LoginGate.WaitAsync(limit.Token).ConfigureAwait(false);
                held = true;
            }

            var login = await _client.AuthenticateAsync(endpoint, credentials, limit.Token).ConfigureAwait(false);
            failure = login.Value is { } session ? await LogOutUntilAsync(endpoint, session, limit).ConfigureAwait(false) : login.Failure;
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            failure = RemoteFailure.Timeout;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold an address. The removal is saved, so it must not fail now.
            _logger.LogWarning("Could not end the login on removed remote server {ServerName} without a live login: {Error}; its JellyMesh device may stay listed there", name, exception.GetType().Name);
            return;
        }
        finally
        {
            if (held)
            {
                runtime!.LoginGate.Release();
            }
        }

        if (failure == RemoteFailure.None)
        {
            _logger.LogInformation("Ended the JellyMesh device on removed remote server {ServerName} by logging in again, as it had no live login", name);
        }
        else
        {
            _logger.LogWarning("Could not end the login on removed remote server {ServerName} without a live login: {Failure}; its JellyMesh device may stay listed there", name, failure);
        }
    }

    // The stored credentials of a server, or null when they do not decrypt or there is no usable key.
    private RemoteCredentials? UnprotectOrNull(Guid serverId, string encryptedCredentials)
    {
        try
        {
            return _vault.Unprotect(serverId, encryptedCredentials);
        }
        catch (Exception exception) when (exception is CryptographicException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // Ends a login that finished after the removal of its server, so that no device stays behind (413). The forgotten
    // runtime leaves too, with any other login it kept: if the removal is not saved, the next call builds a new one.
    private async Task EndLoginOfRemovedAsync(Guid serverId, ServerRuntime? runtime, RemoteEndpoint endpoint, RemoteSession fresh, string serverName, bool freshMayBeRevoked = false)
    {
        var name = LogText.Clean(serverName);
        _logger.LogInformation("Remote server {ServerName} was removed while logging in; that login is ended", name);
        foreach (var failure in await EndFreshAndKeptAsync(serverId, runtime, endpoint, fresh, freshMayBeRevoked).ConfigureAwait(false))
        {
            _logger.LogWarning("Could not end a login on removed remote server {ServerName}: {Failure}; its JellyMesh device may stay listed there", name, failure);
        }
    }

    // Ends the login of an add that was not saved, with any login a call kept meanwhile (419).
    private async Task EndUnsavedAddAsync(RemoteServerConfig server, RemoteEndpoint endpoint, RemoteSession login, Exception error)
    {
        var name = LogText.Clean(server.Name);
        _logger.LogWarning("Cannot save new remote server {ServerName}: {Error}; its login is ended", name, error.GetType().Name);
        var runtime = _runtimes.TryGetValue(server.Id, out var held) ? held : null;
        foreach (var failure in await EndFreshAndKeptAsync(server.Id, runtime, endpoint, login, freshMayBeRevoked: true).ConfigureAwait(false))
        {
            _logger.LogWarning("Could not end a login on unsaved remote server {ServerName}: {Failure}; its JellyMesh device may stay listed there", name, failure);
        }
    }

    // Forgets the runtime and ends the fresh login and the one the runtime kept, under one logout limit. Returns the
    // failures left; a refused token revoked by a later login of this device counts as ended.
    private async Task<IReadOnlyList<RemoteFailure>> EndFreshAndKeptAsync(Guid serverId, ServerRuntime? runtime, RemoteEndpoint endpoint, RemoteSession fresh, bool freshMayBeRevoked)
    {
        RemoteSession? kept = null;
        if (runtime is not null)
        {
            _runtimes.TryRemove(KeyValuePair.Create(serverId, runtime));
            kept = runtime.Forget();
        }

        using var limit = new CancellationTokenSource(LogoutLimit);
        var other = kept is null || ReferenceEquals(kept, fresh) ? null : kept;
        var failures = await Task.WhenAll(
            LogOutUntilAsync(endpoint, fresh, limit),
            other is null ? Task.FromResult(RemoteFailure.None) : LogOutUntilAsync(endpoint, other, limit)).ConfigureAwait(false);

        // A refused kept token was revoked by another login of this device: the fresh one, or one its own path ends.
        if (failures[1] == RemoteFailure.AuthenticationFailed)
        {
            failures[1] = RemoteFailure.None;
        }

        // The add logs in outside the gate, so a later login of this device, which its own path ends, can revoke it.
        if (freshMayBeRevoked && failures[0] == RemoteFailure.AuthenticationFailed)
        {
            failures[0] = RemoteFailure.None;
        }

        return failures.Where(failure => failure != RemoteFailure.None).ToArray();
    }

    // Ends a login that is not kept, such as one whose credentials could not be saved (419), within the logout limit.
    // With refusedIsEnded, a refused token was revoked by a later login and counts as ended.
    private async Task EndUnusedLoginAsync(RemoteEndpoint endpoint, RemoteSession session, string serverName, bool refusedIsEnded)
    {
        var failure = await LogOutWithinLimitAsync(endpoint, session).ConfigureAwait(false);
        if (failure != RemoteFailure.None && !(refusedIsEnded && failure == RemoteFailure.AuthenticationFailed))
        {
            _logger.LogWarning("Could not end an unused login on remote server {ServerName}: {Failure}; it may stay open there until the next login", LogText.Clean(serverName), failure);
        }
    }

    // Sends the logout, and gives up at the logout limit: the remote cannot hold a removal up.
    private async Task<RemoteFailure> LogOutWithinLimitAsync(RemoteEndpoint endpoint, RemoteSession session)
    {
        using var limit = new CancellationTokenSource(LogoutLimit);
        return await LogOutUntilAsync(endpoint, session, limit).ConfigureAwait(false);
    }

    // Sends the logout, and gives up when the limit is reached.
    private async Task<RemoteFailure> LogOutUntilAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationTokenSource limit)
    {
        try
        {
            return (await _client.LogoutAsync(endpoint, session, limit.Token).ConfigureAwait(false)).Failure;
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            return RemoteFailure.Timeout;
        }
    }

    // Lets go of the state of a server removed meanwhile, unless it holds a login that the removal still ends (413). The
    // runtime is forgotten in the same step as the check, so a login kept later is ended by its own path.
    private void DropIfRemoved(Guid serverId, ServerRuntime runtime)
    {
        if (!IsConfigured(serverId) && runtime.TryForgetIfIdle())
        {
            _runtimes.TryRemove(KeyValuePair.Create(serverId, runtime));
        }
    }

    private bool IsRemoving(Guid serverId)
    {
        lock (_removingLock)
        {
            return _removing.ContainsKey(serverId);
        }
    }

    private bool IsConfigured(Guid serverId) => _store.Read(config => config.Servers.Any(server => server.Id == serverId));

    // Stores the Server Id of a server configured before it was read, at its first login (decision 121).
    private void RememberServerId(StoredServer server, string remoteId)
    {
        var stored = _store.Read(config => config.Servers.FirstOrDefault(candidate => candidate.Id == server.Id)?.JellyfinServerId);
        if (stored is null || stored.Length > 0)
        {
            return;
        }

        try
        {
            var heldElsewhere = false;
            _store.Update(config =>
            {
                if (config.Servers.FirstOrDefault(candidate => candidate.Id == server.Id) is { JellyfinServerId.Length: 0 } stored)
                {
                    heldElsewhere = !TryStoreServerId(config, stored, remoteId);
                }
            });
            if (heldElsewhere)
            {
                WarnServerIdHeldElsewhere(server.Name, remoteId);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a path or an address.
            _logger.LogWarning("Cannot save the Server Id of remote server {ServerName}: {Error}", LogText.Clean(server.Name), exception.GetType().Name);
        }
    }

    // Stores a Server Id read after the add unless another configured server holds it (decision 128).
    private static bool TryStoreServerId(PluginConfiguration config, RemoteServerConfig server, string remoteId)
    {
        if (config.Servers.Any(other => other.Id != server.Id && other.JellyfinServerId == remoteId))
        {
            return false;
        }

        server.JellyfinServerId = remoteId;
        return true;
    }

    // Logs a Server Id left unstored because another configured server already holds it (decision 128).
    private void WarnServerIdHeldElsewhere(string serverName, string remoteId)
    {
        _logger.LogWarning(
            "Remote server {ServerName} answered with Server Id {RemoteServerId}, which another configured server holds; it is not stored",
            LogText.Clean(serverName),
            remoteId);
    }

    // The saved Server Id of a server when the remote gave another one, null when the login is accepted (row 433).
    private string? RefusedServerId(Guid serverId, string? remoteId)
    {
        var stored = _store.Read(config => config.Servers.FirstOrDefault(server => server.Id == serverId)?.JellyfinServerId);
        return string.IsNullOrEmpty(stored) || stored == remoteId ? null : stored;
    }

    // Ends a login on which another server answered, and logs both ids with neither the address nor the token (row 433).
    private async Task RefuseOtherServerAsync(RemoteEndpoint endpoint, RemoteSession login, string serverName, string storedId)
    {
        WarnOtherServer(serverName, login.ServerId, storedId);
        await EndUnusedLoginAsync(endpoint, login, serverName, refusedIsEnded: false).ConfigureAwait(false);
    }

    // Logs a login refused because another server answered, with both ids and neither the address nor the token.
    private void WarnOtherServer(string serverName, string? remoteId, string storedId)
    {
        _logger.LogWarning(
            "Remote server {ServerName} answered with Server Id {RemoteServerId}, not the stored {StoredServerId}: another server answers at its address, so the login is refused",
            LogText.Clean(serverName),
            remoteId,
            storedId);
    }

    // Records the outcome of a call, and writes to the activity log when the server goes offline or answers again (49, 130).
    private void Record(ServerRuntime runtime, string serverName, RemoteFailure failure, bool countsForActivity)
    {
        var activity = _activity;
        runtime.Record(
            failure,
            countsForActivity && activity is not null
                ? reachable => activity.Write(reachable ? ActivityEntries.ServerReachable(serverName) : ActivityEntries.ServerOffline(serverName, failure))
                : null);
    }

    private RemoteServerInfo ToInfo(RemoteServerConfig server)
    {
        _runtimes.TryGetValue(server.Id, out var runtime);
        return new RemoteServerInfo(
            server.Id,
            server.Name,
            server.Url,
            server.Mode,
            server.ProtocolVersion,
            server.ImportedLibraryIds.ToList(),
            runtime?.LastFailure,
            runtime?.LastCheckedUtc)
        {
            NeedsPassword = !_vault.CanDecrypt(server.Id, server.EncryptedCredentials)
        };
    }

    private ServerRuntime RuntimeOf(Guid serverId) => _runtimes.GetOrAdd(serverId, _ => new ServerRuntime());

    // The credentials are not copied: a login reads them under the login gate.
    private sealed record StoredServer(Guid Id, string Name, string Url);

    private sealed record SessionAttempt<T>(RemoteResult<T> Attempt, RemoteSession? Session);

    // The saved fields an edit compares with.
    private sealed record SavedServer(string Name, string Url, string JellyfinServerId, IReadOnlyList<string> LibraryIds, LibraryMode LibraryMode);

    // An edit whose fields are valid, with the credentials it logs in with. The password is in no generated text.
    private sealed record EditPlan(Guid ServerId, string Name, string Url, RemoteCredentials Credentials, RemoteEditChanges Changes, SavedServer Saved, IReadOnlyList<string>? RequestedLibraryIds)
    {
        // The saved credentials, or null when they do not decrypt.
        public RemoteCredentials? Stored { get; init; }

        // The library mode the edit saves.
        public LibraryMode LibraryMode { get; init; }
    }

    // What the remote offers with the edited data, and its capabilities for the save.
    private sealed record EditRead(RemoteEditReport Report, RemoteCapabilities? Capabilities);

    // Runs its action once, at the first dispose.
    private sealed class Release(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }

    private sealed class ServerRuntime : IDisposable
    {
        private readonly Lock _gate = new();
        private bool? _reachable;
        private RemoteSession? _session;
        private bool _forgotten;

        public SemaphoreSlim LoginGate { get; } = new(1, 1);

        public RemoteSession? Session
        {
            get
            {
                lock (_gate)
                {
                    return _session;
                }
            }

            set
            {
                lock (_gate)
                {
                    _session = _forgotten ? null : value;
                }
            }
        }

        public RemoteFailure? LastFailure { get; private set; }

        public DateTimeOffset? LastCheckedUtc { get; private set; }

        // Records an outcome. When it changes whether the server is reachable, tells onChange under the lock, so entries keep
        // their order; an answer first seen since start is no change. A call without onChange leaves reachability alone.
        public void Record(RemoteFailure failure, Action<bool>? onChange)
        {
            lock (_gate)
            {
                LastFailure = failure;
                LastCheckedUtc = DateTimeOffset.UtcNow;

                var reachable = onChange is null ? null : ReachabilityOf(failure);
                if (reachable is null || reachable == _reachable)
                {
                    return;
                }

                var known = _reachable is not null;
                _reachable = reachable;
                if (known || !reachable.Value)
                {
                    onChange!(reachable.Value);
                }
            }
        }

        // Keeps a new login, unless the server was removed meanwhile.
        public bool TryKeep(RemoteSession session)
        {
            lock (_gate)
            {
                if (!_forgotten)
                {
                    _session = session;
                }

                return !_forgotten;
            }
        }

        // Forgets the runtime only if it holds no login, in one step: a later login is then not kept.
        public bool TryForgetIfIdle()
        {
            lock (_gate)
            {
                if (_session is null)
                {
                    _forgotten = true;
                }

                return _forgotten;
            }
        }

        // Takes the login out for good: a later one is not kept.
        public RemoteSession? Forget()
        {
            lock (_gate)
            {
                _forgotten = true;
                var session = _session;
                _session = null;
                return session;
            }
        }

        public void Dispose() => LoginGate.Dispose();

        // Offline when nothing answered, reachable when the remote answered. An error status, which a gateway in front of
        // it can send, or a call never sent says nothing about it.
        private static bool? ReachabilityOf(RemoteFailure failure) => failure switch
        {
            RemoteFailure.Unreachable or RemoteFailure.Timeout => false,
            RemoteFailure.Suspended or RemoteFailure.UnknownServer or RemoteFailure.InvalidServer or RemoteFailure.Busy
                or RemoteFailure.ServerError or RemoteFailure.RemovalFailed or RemoteFailure.RemovalFailedPlaybackStopped
                or RemoteFailure.ForgetFailed or RemoteFailure.OtherServer or RemoteFailure.BeingRemoved or RemoteFailure.NameTaken
                or RemoteFailure.RenameUnfinished => null,
            _ => true
        };
    }
}
