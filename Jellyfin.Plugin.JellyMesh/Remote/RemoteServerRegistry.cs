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

        var serverId = Guid.NewGuid();
        var endpoint = EndpointOf(serverId, normalizedUrl);

        var login = await _client.AuthenticateAsync(endpoint, credentials, cancellationToken).ConfigureAwait(false);
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

        var encrypted = _vault.Protect(serverId, credentials);
        if (encrypted is null)
        {
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
        _store.Update(config =>
        {
            standing = remoteId is null ? null : ConfiguredAs(config, remoteId);
            if (standing is null)
            {
                config.Servers.Add(server);
            }
        });
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
            // A call that logged in since the save holds the live login: the remote revoked this one.
            if (runtime.Session is null && (!runtime.TryKeep(login.Value!) || !IsConfigured(serverId)))
            {
                await EndLoginOfRemovedAsync(serverId, runtime, endpoint, login.Value!, server.Name).ConfigureAwait(false);
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
            ? new StoredServer(server.Id, server.Name, server.Url, server.EncryptedCredentials)
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
            var login = await _client.AuthenticateAsync(endpoint, credentials, cancellationToken).ConfigureAwait(false);
            if (!login.IsSuccess)
            {
                _logger.LogWarning("Login on remote server {ServerName} with new credentials failed: {Failure}", LogText.Clean(stored.Name), login.Failure);
                return RemoteResult.Fail<RemoteServerInfo>(login.Failure);
            }

            var encrypted = _vault.Protect(serverId, credentials);
            if (encrypted is null)
            {
                return RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.Suspended);
            }

            var found = false;
            var heldElsewhere = false;
            string? storedId = null;
            _store.Update(config =>
            {
                if (config.Servers.FirstOrDefault(server => server.Id == serverId) is { } server)
                {
                    server.EncryptedCredentials = encrypted;
                    storedId = server.JellyfinServerId;
                    if (server.JellyfinServerId.Length == 0 && login.Value!.ServerId is { } remoteId)
                    {
                        heldElsewhere = !TryStoreServerId(config, server, remoteId);
                    }

                    found = true;
                }
            });
            if (!found)
            {
                await EndLoginOfRemovedAsync(serverId, runtime, endpoint, login.Value!, stored.Name).ConfigureAwait(false);
                return RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.UnknownServer);
            }

            WarnIfOtherServerId(stored.Name, storedId, login.Value!.ServerId);
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
    public async Task<bool> RemoveAsync(Guid serverId)
    {
        RemoteServerConfig? removed = null;
        var position = -1;
        try
        {
            _store.Update(config =>
            {
                removed = config.Servers.FirstOrDefault(candidate => candidate.Id == serverId);
                if (removed is not null)
                {
                    position = config.Servers.IndexOf(removed);
                    config.Servers.RemoveAt(position);
                    HoldLibraries(config, removed);
                }
            });
        }
        catch (Exception exception) when (removed is not null && exception is not OperationCanceledException)
        {
            Restore(removed, position);
            throw;
        }

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

        var found = false;
        _store.Update(config =>
        {
            if (config.Servers.FirstOrDefault(candidate => candidate.Id == serverId) is { } server)
            {
                found = true;
                server.Name = name.Trim();
            }
        });

        return found;
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
            .Select(candidate => new StoredServer(candidate.Id, candidate.Name, candidate.Url, candidate.EncryptedCredentials))
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
    }

    // Puts back in memory a server whose removal was not saved: the file still has it.
    private void Restore(RemoteServerConfig server, int position)
    {
        try
        {
            _store.Update(config =>
            {
                config.ReleaseHeldAutomaticLibraries(server.Id, server.AutomaticLibraryRegistry);
                config.Servers.Insert(Math.Min(position, config.Servers.Count), server);
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError("Cannot save the configuration again after a failed removal: {Error}", exception.GetType().Name);
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

            var credentials = UnprotectOrNull(server.Id, server.EncryptedCredentials);
            if (credentials is null)
            {
                DropIfRemoved(server.Id, runtime);
                return RemoteResult.Fail<RemoteSession>(RemoteFailure.Suspended);
            }

            var login = await _client.AuthenticateAsync(endpoint, credentials, cancellationToken).ConfigureAwait(false);
            if (login.Value is not { } fresh)
            {
                runtime.Session = null;
                DropIfRemoved(server.Id, runtime);
                return login;
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
        if (UnprotectOrNull(removed.Id, removed.EncryptedCredentials) is not { } credentials)
        {
            _logger.LogWarning("Removed remote server {ServerName} had no live login and its stored credentials cannot be read; its JellyMesh device may stay listed there", name);
            return;
        }

        var endpoint = EndpointOf(removed.Id, removed.Url);
        using var limit = new CancellationTokenSource(LogoutLimit);
        var held = false;
        RemoteFailure failure;
        try
        {
            if (runtime is not null)
            {
                await runtime.LoginGate.WaitAsync(limit.Token).ConfigureAwait(false);
                held = true;
            }

            var login = await _client.AuthenticateAsync(endpoint, credentials, limit.Token).ConfigureAwait(false);
            failure = login.Value is { } session ? await LogOutUntilAsync(endpoint, session, limit).ConfigureAwait(false) : login.Failure;

            // A token refused right after it was issued was revoked by a later login of this device, which its path ends.
            if (failure == RemoteFailure.AuthenticationFailed && login.IsSuccess)
            {
                failure = RemoteFailure.None;
            }
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
    private async Task EndLoginOfRemovedAsync(Guid serverId, ServerRuntime? runtime, RemoteEndpoint endpoint, RemoteSession fresh, string serverName)
    {
        RemoteSession? kept = null;
        if (runtime is not null)
        {
            _runtimes.TryRemove(KeyValuePair.Create(serverId, runtime));
            kept = runtime.Forget();
        }

        var name = LogText.Clean(serverName);
        _logger.LogInformation("Remote server {ServerName} was removed while logging in; that login is ended", name);
        using var limit = new CancellationTokenSource(LogoutLimit);
        var other = kept is null || ReferenceEquals(kept, fresh) ? null : kept;
        var failures = await Task.WhenAll(
            LogOutUntilAsync(endpoint, fresh, limit),
            other is null ? Task.FromResult(RemoteFailure.None) : LogOutUntilAsync(endpoint, other, limit)).ConfigureAwait(false);

        // The remote revoked the kept, older login of this device at the fresh one, so a refused token there is ended.
        if (failures[1] == RemoteFailure.AuthenticationFailed)
        {
            failures[1] = RemoteFailure.None;
        }

        foreach (var failure in failures.Where(failure => failure != RemoteFailure.None))
        {
            _logger.LogWarning("Could not end a login on removed remote server {ServerName}: {Failure}; its JellyMesh device may stay listed there", name, failure);
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

    // Lets go of the state of a server removed meanwhile, unless it holds a login that the removal still ends (413).
    private void DropIfRemoved(Guid serverId, ServerRuntime runtime)
    {
        if (runtime.Session is null && !IsConfigured(serverId))
        {
            _runtimes.TryRemove(KeyValuePair.Create(serverId, runtime));
        }
    }

    private bool IsConfigured(Guid serverId) => _store.Read(config => config.Servers.Any(server => server.Id == serverId));

    // Stores the Server Id of a server configured before it was read, at its first login (decision 121).
    private void RememberServerId(StoredServer server, string remoteId)
    {
        var stored = _store.Read(config => config.Servers.FirstOrDefault(candidate => candidate.Id == server.Id)?.JellyfinServerId);
        if (stored is null || stored.Length > 0)
        {
            WarnIfOtherServerId(server.Name, stored, remoteId);
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

    // Logs a login whose Server Id differs from the stored one, which stays (decision 126).
    private void WarnIfOtherServerId(string serverName, string? storedId, string? remoteId)
    {
        if (!string.IsNullOrEmpty(storedId) && remoteId is not null && remoteId != storedId)
        {
            _logger.LogWarning(
                "Remote server {ServerName} answered with Server Id {RemoteServerId}, not the stored {StoredServerId}; the stored one is kept",
                LogText.Clean(serverName),
                remoteId,
                storedId);
        }
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

    private sealed record StoredServer(Guid Id, string Name, string Url, string EncryptedCredentials);

    private sealed record SessionAttempt<T>(RemoteResult<T> Attempt, RemoteSession? Session);

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
                or RemoteFailure.ForgetFailed => null,
            _ => true
        };
    }
}
