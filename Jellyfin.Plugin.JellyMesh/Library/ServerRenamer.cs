using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
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
    private readonly IFederatedFolderLinkChecker _links;
    private readonly IPluginConfigurationStore _store;
    private readonly ILogger<ServerRenamer> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _jobsLock = new();
    private readonly Dictionary<Guid, Task> _jobs = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerRenamer"/> class.
    /// </summary>
    /// <param name="registry">The server registry.</param>
    /// <param name="stubs">The stub moves.</param>
    /// <param name="libraries">The automatic libraries.</param>
    /// <param name="scans">The library scans.</param>
    /// <param name="linker">The version linker.</param>
    /// <param name="retired">The old ids of renamed libraries.</param>
    /// <param name="links">The link check of the server folders.</param>
    /// <param name="store">The configuration store, which keeps a rename that did not finish.</param>
    /// <param name="logger">The logger.</param>
    public ServerRenamer(
        IRemoteServerRegistry registry,
        IServerStubRenamer stubs,
        IServerLibraryManager libraries,
        ILibraryScans scans,
        IVersionLinker linker,
        RetiredLibraryIds retired,
        IFederatedFolderLinkChecker links,
        IPluginConfigurationStore store,
        ILogger<ServerRenamer> logger)
    {
        _registry = registry;
        _stubs = stubs;
        _libraries = libraries;
        _scans = scans;
        _linker = linker;
        _retired = retired;
        _links = links;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool NeedsRestart => !_retired.IsEmpty;

    /// <inheritdoc />
    public ServerRenameState GetState(Guid serverId)
    {
        var busy = IsBusy(serverId);
        return new(busy, _stubs.IsPending(serverId), !busy && _store.Read(config => Find(config, serverId)?.RenameUnfinished ?? false))
        {
            Stuck = _stubs.IsStuck(serverId)
        };
    }

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
    public async Task<ServerRenameOutcome> CancelPendingAsync(Guid serverId)
    {
        if (!_registry.GetServers().Any(server => server.Id == serverId))
        {
            return ServerRenameOutcome.UnknownServer;
        }

        Task<ServerRenameOutcome> job;
        lock (_jobsLock)
        {
            if (_jobs.ContainsKey(serverId))
            {
                return ServerRenameOutcome.Busy;
            }

            job = Task.Run(() => CancelAsync(serverId));
            _jobs[serverId] = job;
        }

        return await job.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private static RemoteServerConfig? Find(PluginConfiguration config, Guid serverId) => config.Servers.FirstOrDefault(server => server.Id == serverId);

    private bool IsBusy(Guid serverId)
    {
        lock (_jobsLock)
        {
            return _jobs.ContainsKey(serverId);
        }
    }

    private async Task<ServerRenameOutcome> CancelAsync(Guid serverId)
    {
        try
        {
            return await _stubs.CancelAsync(serverId, _shutdown.Token).ConfigureAwait(false)
                ? ServerRenameOutcome.Cancelled
                : ServerRenameOutcome.CancelFailed;
        }
        catch (Exception exception)
        {
            // The type only: a message could hold a path.
            _logger.LogError("Cannot cancel the pending rename of a server: {Error}", exception.GetType().Name);
            return ServerRenameOutcome.CancelFailed;
        }
        finally
        {
            lock (_jobsLock)
            {
                _jobs.Remove(serverId);
            }
        }
    }

    private async Task RunAsync(Guid serverId, string name)
    {
        try
        {
            // Saved before the first step and cleared only after the last, so a stop of the server leaves it set.
            MarkUnfinished(serverId, true);
            if (await RenameAsync(serverId, name, _shutdown.Token).ConfigureAwait(false))
            {
                MarkUnfinished(serverId, false);
            }
        }
        catch (OperationCanceledException)
        {
            // The server is shutting down: the next scan finishes the stub moves.
        }
        catch (Exception exception)
        {
            // The type only: a message could hold a path.
            _logger.LogError("Cannot rename a server: {Error}", exception.GetType().Name);
        }
        finally
        {
            lock (_jobsLock)
            {
                _jobs.Remove(serverId);
            }
        }
    }

    // False when a library was left with its old name or users with its old id: the page shows it, the stubs go on.
    private async Task<bool> RenameAsync(Guid serverId, string name, CancellationToken cancellationToken)
    {
        if (!_registry.Rename(serverId, name))
        {
            return true;
        }

        // In no library the stubs have no items or watch state to carry: their files are renamed at once (rows 288, 316).
        if (InNoLibrary(serverId))
        {
            await _stubs.RelabelInPlaceAsync(serverId, name, cancellationToken).ConfigureAwait(false);
            try
            {
                return await _libraries.RenameAsync(serverId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // A renamed library stops the library monitor, which only a scan starts again.
                await _scans.QueueAndWaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await _stubs.RelabelAsync(serverId, name, cancellationToken).ConfigureAwait(false);
        bool done;
        try
        {
            done = await _libraries.RenameAsync(serverId, cancellationToken).ConfigureAwait(false);
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

        return done;
    }

    // Only when the libraries could be read and none has either folder of the server or one over them.
    private bool InNoLibrary(Guid serverId)
    {
        var root = _store.Read(config => config.FederatedFolderPath);
        if (root.Length == 0)
        {
            return false;
        }

        var report = _links.Check(root, [serverId]);
        return report.IsChecked && report.Servers.Count > 0
            && report.Covering.Count == 0
            && report.Servers.All(server => server.Movies.Link == FederatedPathLink.NotLinked && server.Series.Link == FederatedPathLink.NotLinked);
    }

    private void MarkUnfinished(Guid serverId, bool unfinished) => _store.Update(config =>
    {
        if (Find(config, serverId) is { } server)
        {
            server.RenameUnfinished = unfinished;
        }
    });

    private async Task FinishAsync(CancellationToken cancellationToken)
    {
        await _stubs.FinishAsync(cancellationToken).ConfigureAwait(false);
        await _linker.LinkAsync(false, cancellationToken).ConfigureAwait(false);
    }
}
