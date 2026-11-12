using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly IPluginConfigurationStore _store;
    private readonly ICredentialVault _vault;
    private readonly IJellyfinRemoteClient _client;
    private readonly ILogger<RemoteServerRegistry> _logger;
    private readonly ConcurrentDictionary<Guid, ServerRuntime> _runtimes = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteServerRegistry"/> class.
    /// </summary>
    /// <param name="store">The configuration store.</param>
    /// <param name="vault">The credential vault.</param>
    /// <param name="client">The remote client.</param>
    /// <param name="logger">The logger.</param>
    public RemoteServerRegistry(
        IPluginConfigurationStore store,
        ICredentialVault vault,
        IJellyfinRemoteClient client,
        ILogger<RemoteServerRegistry> logger)
    {
        _store = store;
        _vault = vault;
        _client = client;
        _logger = logger;
    }

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
            _logger.LogWarning("Login on new remote server {ServerName} failed: {Failure}", name, login.Failure);
            return RemoteResult.Fail<RemoteServerInfo>(login.Failure);
        }

        // The Server Id of the remote says whether it is configured already, or was removed and comes back (116, 120).
        var remoteId = login.Value!.ServerId;
        string? recognizedAs = null;
        if (remoteId is not null)
        {
            var known = _store.Read(config => Recognize(config, remoteId));
            if (known.Configured is { } configured)
            {
                await EndSessionAsync(endpoint, login.Value, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Remote server {ServerName} is already configured as {ConfiguredName}", name, configured.Name);
                return RemoteResult.Fail(RemoteFailure.AlreadyConfigured, configured);
            }

            if (known.Former is { } former)
            {
                // The login so far used a device of its own: the server logs in again as the device it had.
                await EndSessionAsync(endpoint, login.Value, cancellationToken).ConfigureAwait(false);
                serverId = former.Id;
                endpoint = EndpointOf(serverId, normalizedUrl);
                login = await _client.AuthenticateAsync(endpoint, credentials, cancellationToken).ConfigureAwait(false);
                if (!login.IsSuccess)
                {
                    _logger.LogWarning("Login on returning remote server {ServerName} failed: {Failure}", name, login.Failure);
                    return RemoteResult.Fail<RemoteServerInfo>(login.Failure);
                }

                recognizedAs = former.Name;
            }
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
            standing = remoteId is null ? null : Recognize(config, remoteId).Configured;
            standing ??= config.Servers.FirstOrDefault(candidate => candidate.Id == serverId) is { } taken ? ToInfo(taken) : null;
            if (standing is not null)
            {
                return;
            }

            RemoveFormer(config, candidate => candidate.Id == serverId || candidate.JellyfinServerId == remoteId);
            config.Servers.Add(server);
        });
        if (standing is not null)
        {
            await EndSessionAsync(endpoint, login.Value!, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Remote server {ServerName} is already configured as {ConfiguredName}", name, standing.Name);
            return RemoteResult.Fail(RemoteFailure.AlreadyConfigured, standing);
        }

        var runtime = RuntimeOf(serverId);
        runtime.Session = login.Value;
        runtime.Record(RemoteFailure.None);

        if (recognizedAs is null)
        {
            _logger.LogInformation("Added remote server {ServerName} in {Mode} mode", server.Name, server.Mode);
        }
        else
        {
            _logger.LogInformation("Added remote server {ServerName} in {Mode} mode, recognized as {FormerName}", server.Name, server.Mode, recognizedAs);
        }

        return RemoteResult.Success<RemoteServerInfo>(_store.Read(_ => ToInfo(server) with { RecognizedAs = recognizedAs }));
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

        var login = await _client.AuthenticateAsync(EndpointOf(serverId, stored.Url), credentials, cancellationToken).ConfigureAwait(false);
        if (!login.IsSuccess)
        {
            _logger.LogWarning("Login on remote server {ServerName} with new credentials failed: {Failure}", stored.Name, login.Failure);
            return RemoteResult.Fail<RemoteServerInfo>(login.Failure);
        }

        var encrypted = _vault.Protect(serverId, credentials);
        if (encrypted is null)
        {
            return RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.Suspended);
        }

        var found = false;
        _store.Update(config =>
        {
            if (config.Servers.FirstOrDefault(server => server.Id == serverId) is { } server)
            {
                server.EncryptedCredentials = encrypted;
                if (server.JellyfinServerId.Length == 0 && login.Value!.ServerId is { } remoteId)
                {
                    server.JellyfinServerId = remoteId;
                }

                found = true;
            }
        });
        if (!found)
        {
            return RemoteResult.Fail<RemoteServerInfo>(RemoteFailure.UnknownServer);
        }

        var runtime = RuntimeOf(serverId);
        runtime.Session = login.Value;
        runtime.Record(RemoteFailure.None);
        _logger.LogInformation("Updated the credentials of remote server {ServerName}", stored.Name);
        return RemoteResult.Success(_store.Read(config => ToInfo(config.Servers.First(server => server.Id == serverId))));
    }

    /// <inheritdoc />
    public bool Remove(Guid serverId)
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
                    Remember(config, removed);
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
        _runtimes.TryRemove(serverId, out _);
        return true;
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

        _logger.LogInformation("Remote server {ServerName} is now in {Mode} mode", known.Name, detected.Value.Mode);
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
    public async Task<RemoteResult<T>> RunWithSessionAsync<T>(
        Guid serverId,
        Func<RemoteEndpoint, RemoteSession, CancellationToken, Task<RemoteResult<T>>> call,
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

        runtime.Record(result.Attempt.Failure);
        if (!result.Attempt.IsSuccess)
        {
            _logger.LogWarning("Call to remote server {ServerName} failed: {Failure}", server.Name, result.Attempt.Failure);
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
                RemoveFormer(config, candidate => candidate.Id == server.Id);
                config.Servers.Insert(Math.Min(position, config.Servers.Count), server);
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError("Cannot save the configuration again after a failed removal: {Error}", exception.GetType().Name);
        }
    }

    // Keeps the Server Id of a removed server, so that the same remote added again gets its identifier back (116).
    private static void Remember(PluginConfiguration config, RemoteServerConfig removed)
    {
        if (removed.JellyfinServerId.Length == 0)
        {
            return;
        }

        RemoveFormer(config, candidate => candidate.Id == removed.Id || candidate.JellyfinServerId == removed.JellyfinServerId);
        config.FormerServers.Add(new FormerServerConfig { Id = removed.Id, JellyfinServerId = removed.JellyfinServerId, Name = removed.Name });
    }

    private static void RemoveFormer(PluginConfiguration config, Func<FormerServerConfig, bool> match)
    {
        foreach (var former in config.FormerServers.Where(match).ToList())
        {
            config.FormerServers.Remove(former);
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

            RemoteCredentials? credentials;
            try
            {
                credentials = _vault.Unprotect(server.Id, server.EncryptedCredentials);
            }
            catch (CryptographicException)
            {
                credentials = null;
            }

            if (credentials is null)
            {
                return RemoteResult.Fail<RemoteSession>(RemoteFailure.Suspended);
            }

            var login = await _client.AuthenticateAsync(endpoint, credentials, cancellationToken).ConfigureAwait(false);
            runtime.Session = login.Value;
            if (login.Value?.ServerId is { } remoteId)
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

        _logger.LogWarning("Capability detection on remote server {ServerName} failed: {Failure}", serverName, result.Failure);
        return new RemoteCapabilities(RemoteMode.Unknown, 0);
    }

    // The configured server that stands for a remote, or the removed one it was. A record whose server is configured
    // again, as after a file put back by hand, counts as configured.
    private Recognition Recognize(PluginConfiguration config, string remoteId)
    {
        var configured = config.Servers.FirstOrDefault(server => server.JellyfinServerId == remoteId);
        var former = config.FormerServers.LastOrDefault(candidate => candidate.JellyfinServerId == remoteId);
        configured ??= former is null ? null : config.Servers.FirstOrDefault(server => server.Id == former.Id);
        return configured is null
            ? new Recognition(null, former is null ? null : new FormerServer(former.Id, former.Name))
            : new Recognition(ToInfo(configured), null);
    }

    // Ends a login that will not be used, so that its device does not stay listed on the remote.
    private async Task EndSessionAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        var result = await _client.LogoutAsync(endpoint, session, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.LogInformation("Could not end an unused login on a remote server: {Failure}", result.Failure);
        }
    }

    // Stores the Server Id of a server configured before it was read, at its first login (decision 121).
    private void RememberServerId(StoredServer server, string remoteId)
    {
        if (!_store.Read(config => config.Servers.Any(candidate => candidate.Id == server.Id && candidate.JellyfinServerId.Length == 0)))
        {
            return;
        }

        try
        {
            _store.Update(config =>
            {
                if (config.Servers.FirstOrDefault(candidate => candidate.Id == server.Id) is { JellyfinServerId.Length: 0 } stored)
                {
                    stored.JellyfinServerId = remoteId;
                }
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a path or an address.
            _logger.LogWarning("Cannot save the Server Id of remote server {ServerName}: {Error}", server.Name, exception.GetType().Name);
        }
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

    private sealed record FormerServer(Guid Id, string Name);

    private sealed record Recognition(RemoteServerInfo? Configured, FormerServer? Former);

    private sealed record SessionAttempt<T>(RemoteResult<T> Attempt, RemoteSession? Session);

    private sealed class ServerRuntime : IDisposable
    {
        public SemaphoreSlim LoginGate { get; } = new(1, 1);

        public RemoteSession? Session { get; set; }

        public RemoteFailure? LastFailure { get; private set; }

        public DateTimeOffset? LastCheckedUtc { get; private set; }

        public void Record(RemoteFailure failure)
        {
            LastFailure = failure;
            LastCheckedUtc = DateTimeOffset.UtcNow;
        }

        public void Dispose() => LoginGate.Dispose();
    }
}
