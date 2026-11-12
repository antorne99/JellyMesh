using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Renames a server with its stubs and automatic libraries in the background, then scans and links the versions (Q38,
/// §10 rows 84 and 85).
/// </summary>
public sealed class ServerRenamer : IServerRenamer, IDisposable
{
    private readonly IRemoteServerRegistry _registry;
    private readonly IServerStubRenamer _stubs;
    private readonly IServerLibraryManager _libraries;
    private readonly ILibraryScans _scans;
    private readonly IVersionLinker _linker;
    private readonly RetiredLibraryIds _retired;
    private readonly ILogger<ServerRenamer> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _jobsLock = new();
    private readonly Dictionary<Guid, Task> _jobs = [];
    private readonly ConcurrentDictionary<Guid, bool> _failed = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerRenamer"/> class.
    /// </summary>
    /// <param name="registry">The server registry.</param>
    /// <param name="stubs">The stub moves.</param>
    /// <param name="libraries">The automatic libraries.</param>
    /// <param name="scans">The library scans.</param>
    /// <param name="linker">The version linker.</param>
    /// <param name="retired">The old ids of renamed libraries.</param>
    /// <param name="logger">The logger.</param>
    public ServerRenamer(
        IRemoteServerRegistry registry,
        IServerStubRenamer stubs,
        IServerLibraryManager libraries,
        ILibraryScans scans,
        IVersionLinker linker,
        RetiredLibraryIds retired,
        ILogger<ServerRenamer> logger)
    {
        _registry = registry;
        _stubs = stubs;
        _libraries = libraries;
        _scans = scans;
        _linker = linker;
        _retired = retired;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool NeedsRestart => !_retired.IsEmpty;

    /// <inheritdoc />
    public ServerRenameState GetState(Guid serverId) =>
        new(IsBusy(serverId), _stubs.IsPending(serverId), _failed.GetValueOrDefault(serverId));

    /// <inheritdoc />
    public ServerRenameRequest Rename(Guid serverId, string? name, bool confirmed)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ServerRenameRequest.Refused(ServerRenameOutcome.InvalidName);
        }

        if (!_registry.GetServers().Any(server => server.Id == serverId))
        {
            return ServerRenameRequest.Refused(ServerRenameOutcome.UnknownServer);
        }

        // A move waiting for its scan knows only the old label, so a second rename waits for it.
        if (IsBusy(serverId) || _stubs.IsPending(serverId))
        {
            return ServerRenameRequest.Refused(ServerRenameOutcome.Busy);
        }

        if (!confirmed)
        {
            var libraries = _libraries.GetState(serverId).Libraries.Select(library => library.Name).ToList();
            return new ServerRenameRequest(ServerRenameOutcome.ConfirmationRequired, Task.CompletedTask, libraries);
        }

        var trimmed = name.Trim();
        lock (_jobsLock)
        {
            if (_jobs.ContainsKey(serverId))
            {
                return ServerRenameRequest.Refused(ServerRenameOutcome.Busy);
            }

            var job = Task.Run(() => RunAsync(serverId, trimmed));
            _jobs[serverId] = job;
            return new ServerRenameRequest(ServerRenameOutcome.Started, job, []);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private bool IsBusy(Guid serverId)
    {
        lock (_jobsLock)
        {
            return _jobs.ContainsKey(serverId);
        }
    }

    private async Task RunAsync(Guid serverId, string name)
    {
        try
        {
            _failed.TryRemove(serverId, out _);
            await RenameAsync(serverId, name, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The server is shutting down: the next scan finishes the stub moves.
        }
        catch (Exception exception)
        {
            // The type only: a message could hold a path.
            _logger.LogError("Cannot rename a server: {Error}", exception.GetType().Name);
            _failed[serverId] = true;
        }
        finally
        {
            lock (_jobsLock)
            {
                _jobs.Remove(serverId);
            }
        }
    }

    private async Task RenameAsync(Guid serverId, string name, CancellationToken cancellationToken)
    {
        if (!_registry.Rename(serverId, name))
        {
            return;
        }

        await _stubs.RelabelAsync(serverId, name, cancellationToken).ConfigureAwait(false);
        try
        {
            // A false answer is on the page through the state of the libraries; the stubs go on.
            await _libraries.RenameAsync(serverId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The scan also starts the library monitor the rename stopped, and makes the new stubs.
            await _scans.QueueAndWaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await FinishAsync(cancellationToken).ConfigureAwait(false);
        if (_stubs.IsPending(serverId))
        {
            // The old items go with this scan, which then links their groups again (row 85).
            await _scans.QueueAndWaitAsync(cancellationToken).ConfigureAwait(false);
            await FinishAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FinishAsync(CancellationToken cancellationToken)
    {
        await _stubs.FinishAsync(cancellationToken).ConfigureAwait(false);
        await _linker.LinkAsync(false, cancellationToken).ConfigureAwait(false);
    }
}
