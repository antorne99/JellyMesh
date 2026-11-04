using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// <see cref="ICatalogSyncService"/>. Each server is read whole before its entries are touched, so a server that fails
/// keeps what it had and is only marked. Removing what is really gone is left to the reconciliation.
/// </summary>
public sealed class CatalogSyncService : ICatalogSyncService, IDisposable
{
    /// <summary>
    /// How far before the start of the last read the next incremental one begins. The clock of the remote has a one
    /// second grain, and it stamps an item before it commits it, so a read can start between the two. Reading an item
    /// twice costs nothing.
    /// </summary>
    public static readonly TimeSpan ChangeOverlap = TimeSpan.FromMinutes(5);

    private readonly IRemoteServerRegistry _registry;
    private readonly IRemoteCatalogReader _reader;
    private readonly IFederationIndex _index;
    private readonly ICredentialVault _vault;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CatalogSyncService> _logger;
    private readonly CancellationTokenSource _shutdown = new();

    private int _busy;
    private volatile RunState _run = new(false, false, null, null);

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogSyncService"/> class.
    /// </summary>
    /// <param name="registry">The server registry.</param>
    /// <param name="reader">The catalog reader.</param>
    /// <param name="index">The index.</param>
    /// <param name="vault">The credential vault.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    public CatalogSyncService(
        IRemoteServerRegistry registry,
        IRemoteCatalogReader reader,
        IFederationIndex index,
        ICredentialVault vault,
        TimeProvider timeProvider,
        ILogger<CatalogSyncService> logger)
    {
        _registry = registry;
        _reader = reader;
        _index = index;
        _vault = vault;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public SyncStartResult TryStart()
    {
        var claim = TryClaim();
        if (claim == SyncStartResult.Started)
        {
            var cancellationToken = _shutdown.Token;
            _ = Task.Run(() => RunGuardedAsync(cancellationToken), CancellationToken.None);
        }

        return claim;
    }

    /// <inheritdoc />
    public async Task<SyncStartResult> RunAsync(CancellationToken cancellationToken)
    {
        var claim = TryClaim();
        if (claim == SyncStartResult.Started)
        {
            await SyncAllAsync(SyncServerAsync, cancellationToken).ConfigureAwait(false);
        }

        return claim;
    }

    /// <inheritdoc />
    public async Task<SyncStartResult> RunIncrementalAsync(CancellationToken cancellationToken)
    {
        var claim = TryClaim();
        if (claim == SyncStartResult.Started)
        {
            await SyncAllAsync(SyncChangesAsync, cancellationToken).ConfigureAwait(false);
        }

        return claim;
    }

    /// <inheritdoc />
    public SyncStatus GetStatus()
    {
        var snapshot = _index.Snapshot;
        var run = _run;
        var servers = _registry.GetServers().Select(server => ToReport(server, snapshot.Find(server.Id))).ToList();
        return new SyncStatus(run.IsRunning, snapshot.Health, run.IndexSaveFailed, run.StartedUtc, run.FinishedUtc, servers);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private static bool CoversSameLibraries(SyncMark mark, RemoteServerInfo server)
    {
        return new HashSet<string>(mark.LibraryIds, StringComparer.Ordinal).SetEquals(server.ImportedLibraryIds);
    }

    private static ServerSyncReport ToReport(RemoteServerInfo server, ServerCatalog? catalog)
    {
        int Count(IndexedItemKind kind) => catalog?.Items.Count(item => item.Kind == kind) ?? 0;

        return new ServerSyncReport(
            server.Id,
            server.Name,
            catalog?.State,
            catalog?.LastFailure,
            catalog?.LastAttemptUtc,
            catalog?.LastSuccessUtc,
            catalog is { State: ServerSyncState.Stale, LastFailure: RemoteFailure.Unreachable or RemoteFailure.Timeout },
            Count(IndexedItemKind.Movie),
            Count(IndexedItemKind.Series),
            Count(IndexedItemKind.Episode));
    }

    private SyncStartResult TryClaim()
    {
        if (_vault.GetKeyState() != KeyState.Ok)
        {
            return SyncStartResult.Suspended;
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return SyncStartResult.AlreadyRunning;
        }

        _run = _run with { IsRunning = true, IndexSaveFailed = false, StartedUtc = _timeProvider.GetUtcNow() };
        return SyncStartResult.Started;
    }

    private async Task RunGuardedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SyncAllAsync(SyncServerAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Catalog sync cancelled");
        }
        catch (Exception exception)
        {
            // The type only: a message could hold a remote address.
            _logger.LogError("Catalog sync stopped: {Error}", exception.GetType().Name);
        }
    }

    private async Task SyncAllAsync(Func<RemoteServerInfo, CancellationToken, Task> syncServer, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var server in _registry.GetServers())
            {
                await syncServer(server, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _run = _run with { IsRunning = false, FinishedUtc = _timeProvider.GetUtcNow() };
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task SyncServerAsync(RemoteServerInfo server, CancellationToken cancellationToken)
    {
        var started = await ReadClockAsync(server, cancellationToken).ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            await MarkFailedAsync(server, started.Failure, cancellationToken).ConfigureAwait(false);
            return;
        }

        var read = await _reader.ReadAsync(server, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            await MarkFailedAsync(server, read.Failure, cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("Read {Count} catalog entries from remote server {ServerName}", read.Value!.Count, server.Name);
        var mark = new SyncMark(started.Value, server.ImportedLibraryIds);
        TrackSave(await _index.ReplaceServerAsync(server.Id, read.Value, _timeProvider.GetUtcNow(), mark, cancellationToken).ConfigureAwait(false));
    }

    private async Task SyncChangesAsync(RemoteServerInfo server, CancellationToken cancellationToken)
    {
        var catalog = _index.Snapshot.Find(server.Id);
        if (catalog is not { State: ServerSyncState.Complete, Mark: { } mark } || !CoversSameLibraries(mark, server))
        {
            await SyncServerAsync(server, cancellationToken).ConfigureAwait(false);
            return;
        }

        var started = await ReadClockAsync(server, cancellationToken).ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            await MarkFailedAsync(server, started.Failure, cancellationToken).ConfigureAwait(false);
            return;
        }

        var known = catalog.Items.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
        var read = await _reader.ReadChangesAsync(server, mark.ReadStartedUtc - ChangeOverlap, known, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            await MarkFailedAsync(server, read.Failure, cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("Read {Count} changed catalog entries from remote server {ServerName}", read.Value!.Count, server.Name);
        var next = new SyncMark(started.Value, server.ImportedLibraryIds);
        TrackSave(await _index.MergeServerAsync(server.Id, read.Value, _timeProvider.GetUtcNow(), next, cancellationToken).ConfigureAwait(false));
    }

    private async Task<RemoteResult<DateTimeOffset>> ReadClockAsync(RemoteServerInfo server, CancellationToken cancellationToken)
    {
        var clock = await _reader.GetServerTimeAsync(server, cancellationToken).ConfigureAwait(false);
        return clock.IsSuccess
            ? RemoteResult.Success(clock.Value ?? _timeProvider.GetUtcNow())
            : RemoteResult.Fail<DateTimeOffset>(clock.Failure);
    }

    private async Task MarkFailedAsync(RemoteServerInfo server, RemoteFailure failure, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Could not read the catalog of remote server {ServerName}: {Failure}. Its entries are kept", server.Name, failure);
        TrackSave(await _index.MarkServerFailedAsync(server.Id, failure, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false));
    }

    private void TrackSave(bool saved)
    {
        if (!saved)
        {
            _run = _run with { IndexSaveFailed = true };
        }
    }

    private sealed record RunState(bool IsRunning, bool IndexSaveFailed, DateTimeOffset? StartedUtc, DateTimeOffset? FinishedUtc);
}
