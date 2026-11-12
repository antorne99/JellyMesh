using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Activity;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
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

    /// <summary>
    /// The longest one server may take in a sync. Servers are read one at a time and a request can last its whole timeout,
    /// so a remote that answers as slowly as it can would otherwise hold back the others for days. Well over a first read
    /// of a very large library on a slow server.
    /// </summary>
    public static readonly TimeSpan MaxServerRead = TimeSpan.FromHours(4);

    private readonly IRemoteServerRegistry _registry;
    private readonly IRemoteCatalogReader _reader;
    private readonly IFederationIndex _index;
    private readonly ICredentialVault _vault;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CatalogSyncService> _logger;
    private readonly ILibraryMaterializer? _materializer;
    private readonly StubRegistry? _stubs;
    private readonly IActivityWriter? _activity;
    private readonly IServerLibraryManager? _libraries;
    private readonly IVersionLinker? _linker;
    private readonly ISyncHistory? _history;
    private readonly IPluginConfigurationStore? _store;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<Guid, string> _feedCursors = new();
    private readonly ConcurrentDictionary<Guid, KeyValuePair<string, RemoteFailure>> _runFailures = new();
    private readonly ConcurrentDictionary<Guid, int> _runReads = new();
    private readonly UnreadablePasswords _unreadablePasswords = new();

    private TimeSpan _serverReadLimit = MaxServerRead;
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
    /// <param name="materializer">What brings the federated folder in step after a sync.</param>
    /// <param name="stubs">The stub registry, which remembers the servers the administrator removed.</param>
    /// <param name="activity">Where completed and failed syncs are written.</param>
    /// <param name="libraries">The libraries kept for each server, followed after every sync.</param>
    /// <param name="linker">What links the copies of a title as versions after the materialization.</param>
    /// <param name="history">Where each run is recorded.</param>
    /// <param name="store">The configuration, which says whether collections are read.</param>
    public CatalogSyncService(
        IRemoteServerRegistry registry,
        IRemoteCatalogReader reader,
        IFederationIndex index,
        ICredentialVault vault,
        TimeProvider timeProvider,
        ILogger<CatalogSyncService> logger,
        ILibraryMaterializer? materializer = null,
        StubRegistry? stubs = null,
        IActivityWriter? activity = null,
        IServerLibraryManager? libraries = null,
        IVersionLinker? linker = null,
        ISyncHistory? history = null,
        IPluginConfigurationStore? store = null)
    {
        _registry = registry;
        _reader = reader;
        _index = index;
        _vault = vault;
        _timeProvider = timeProvider;
        _logger = logger;
        _materializer = materializer;
        _stubs = stubs;
        _activity = activity;
        _libraries = libraries;
        _linker = linker;
        _history = history;
        _store = store;
    }

    /// <summary>
    /// Sets the longest one server may take in a sync.
    /// </summary>
    internal TimeSpan ServerReadLimit
    {
        set => _serverReadLimit = value;
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
        ReportUnreadablePasswords();
        var claim = TryClaim();
        if (claim == SyncStartResult.Started)
        {
            // The nightly reconciliation also checks every stub's item: the safety net for an item left on an old URL.
            await SyncAllAsync(SyncServerAsync, SyncRunKind.Reconciliation, true, cancellationToken).ConfigureAwait(false);
        }

        return claim;
    }

    /// <inheritdoc />
    public async Task<SyncStartResult> RunIncrementalAsync(CancellationToken cancellationToken)
    {
        ReportUnreadablePasswords();
        var claim = TryClaim();
        if (claim == SyncStartResult.Started)
        {
            await SyncAllAsync(SyncChangesAsync, SyncRunKind.Incremental, false, cancellationToken).ConfigureAwait(false);
        }

        return claim;
    }

    /// <inheritdoc />
    public Task<bool> RemoveServerAsync(Guid serverId, Func<Task<bool>> remove, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remove);
        return _stubs?.RemoveServerAsync(serverId, remove, cancellationToken) ?? remove();
    }

    /// <inheritdoc />
    public Task<int> ForgetMissingServersAsync(CancellationToken cancellationToken)
    {
        var known = _index.Snapshot.Servers.Select(catalog => catalog.ServerId).Concat(_feedCursors.Keys).ToList();
        return _stubs?.ForgetServersAsync(() => _registry.GetServers().Select(server => server.Id).ToHashSet(), known, cancellationToken)
            ?? Task.FromResult(0);
    }

    /// <inheritdoc />
    public void StartRemovalCleanup()
    {
        var cancellationToken = _shutdown.Token;
        _ = Task.Run(() => CleanUpAfterRemovalAsync(cancellationToken), CancellationToken.None);
    }

    /// <inheritdoc />
    public void PrepareServerFolders() => _materializer?.PrepareServerFolders();

    /// <inheritdoc />
    public SyncStatus GetStatus()
    {
        var snapshot = _index.Snapshot;
        var run = _run;
        var servers = _registry.GetServers().Select(server => ToReport(server, snapshot.Find(server.Id))).ToList();
        var report = _materializer?.LastReport;
        return new SyncStatus(run.IsRunning, snapshot.Health, run.IndexSaveFailed, run.StartedUtc, run.FinishedUtc, servers)
        {
            HeldLibraries = report?.Held ?? [],
            UnconfiguredStubs = report?.Unconfigured ?? 0
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    // Takes no claim: a sync that is running must not delay this, and the materializer lets one run at a time.
    internal async Task CleanUpAfterRemovalAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DropRemovedServersAsync(cancellationToken).ConfigureAwait(false);
            await MaterializeAsync(false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Cleanup after removing a server cancelled");
        }
        catch (Exception exception)
        {
            // The type only: a message could hold a remote address.
            _logger.LogError("Cleanup after removing a server stopped: {Error}", exception.GetType().Name);
        }
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
            MarkLibraryFollowSkipped();
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
            await SyncAllAsync(SyncServerAsync, SyncRunKind.Reconciliation, false, cancellationToken).ConfigureAwait(false);
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

    // A sync that was reading a server when it was removed stores its catalog afterwards, so this also runs after each sync.
    // Only servers the administrator removed lose their catalogs: one missing from a reset configuration may come back.
    private async Task DropRemovedServersAsync(CancellationToken cancellationToken)
    {
        var configured = _registry.GetServers().Select(server => server.Id).ToHashSet();
        var removed = (_stubs?.State.RemovedServers ?? []).Where(serverId => !configured.Contains(serverId)).ToHashSet();
        foreach (var serverId in _feedCursors.Keys.Where(removed.Contains))
        {
            _feedCursors.TryRemove(serverId, out _);
        }

        await _index.DropServersAsync(removed, cancellationToken).ConfigureAwait(false);
    }

    private async Task SyncAllAsync(Func<RemoteServerInfo, CancellationToken, Task> syncServer, SyncRunKind kind, bool verifyEveryStub, CancellationToken cancellationToken)
    {
        var started = _run.StartedUtc ?? _timeProvider.GetUtcNow();
        IReadOnlyList<RemoteServerInfo> servers = [];
        var materialized = new Materialized(null, false);
        SyncRunOutcome? stopped = null;
        var followed = false;
        try
        {
            _runFailures.Clear();
            _runReads.Clear();
            servers = _registry.GetServers();
            foreach (var server in servers)
            {
                await SyncWithinLimitAsync(server, syncServer, cancellationToken).ConfigureAwait(false);
            }

            await DropRemovedServersAsync(cancellationToken).ConfigureAwait(false);
            materialized = await MaterializeAsync(verifyEveryStub, cancellationToken).ConfigureAwait(false);
            followed = await FollowServerLibrariesAsync(cancellationToken).ConfigureAwait(false);
            if (servers.Count > 0)
            {
                WriteActivity(ActivityEntries.SyncCompleted(servers.Count, _runFailures.Values.ToList()));
            }
        }
        catch (OperationCanceledException)
        {
            stopped = SyncRunOutcome.Cancelled;
            throw;
        }
        catch (Exception)
        {
            stopped = SyncRunOutcome.Failed;
            throw;
        }
        finally
        {
            if (!followed)
            {
                MarkLibraryFollowSkipped();
            }

            // Before the run shows finished: the page reads the history again when it sees a new end.
            await RecordAsync(kind, started, servers, materialized, stopped).ConfigureAwait(false);
            _run = _run with { IsRunning = false, FinishedUtc = _timeProvider.GetUtcNow() };
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    // Saving the history never fails the sync, and a sync with no server is not recorded (decision 189).
    // The type only: a message could hold a remote address.
    private async Task RecordAsync(SyncRunKind kind, DateTimeOffset started, IReadOnlyList<RemoteServerInfo> servers, Materialized materialized, SyncRunOutcome? stopped)
    {
        if (_history is null || servers.Count == 0)
        {
            return;
        }

        try
        {
            var report = materialized.Report;
            var rows = servers.Select(server =>
            {
                var counts = report?.ByServer.GetValueOrDefault(server.Id) ?? new StubCounts(0, 0, 0, 0);
                RemoteFailure? failure = _runFailures.TryGetValue(server.Id, out var failed) ? failed.Value : null;
                return new SyncRunServer(server.Id, server.Name, _runReads.GetValueOrDefault(server.Id), counts.Created, counts.Updated, counts.Removed, counts.Failed, failure);
            }).ToList();
            var run = new SyncRun(started, (long)(_timeProvider.GetUtcNow() - started).TotalMilliseconds, kind, stopped ?? OutcomeOf(rows, report, materialized.Failed), rows)
            {
                Created = report?.Created ?? 0,
                Updated = report?.Updated ?? 0,
                Removed = report?.Removed ?? 0,
                Failed = report?.Failed ?? 0,
                IndexSaveFailed = _run.IndexSaveFailed,
                MaterializationFailed = materialized.Failed
            };
            await _history.AddAsync(run, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError("Could not save the sync history: {Error}", exception.GetType().Name);
        }
    }

    private SyncRunOutcome OutcomeOf(IReadOnlyList<SyncRunServer> rows, MaterializationReport? report, bool materializationFailed)
    {
        if (materializationFailed || (rows.Count > 0 && rows.All(row => row.Failure is not null)))
        {
            return SyncRunOutcome.Failed;
        }

        return rows.Any(row => row.Failure is not null) || _run.IndexSaveFailed || report?.Failed > 0
            ? SyncRunOutcome.PartlyFailed
            : SyncRunOutcome.Succeeded;
    }

    // What goes wrong with one server stays with it: the others are read, and the materialization still runs.
    private async Task SyncWithinLimitAsync(RemoteServerInfo server, Func<RemoteServerInfo, CancellationToken, Task> syncServer, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_serverReadLimit);
        try
        {
            // With nothing chosen there is nothing to read, and no reason to call the remote at all.
            var current = server.ImportedLibraryIds.Count == 0
                ? server
                : await _registry.RefreshCapabilitiesAsync(server.Id, limit.Token).ConfigureAwait(false) ?? server;
            await syncServer(current, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await MarkFailedAsync(server, RemoteFailure.Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a remote address.
            _logger.LogError("Sync of remote server {ServerName} stopped: {Error}", server.Name, exception.GetType().Name);
            await MarkFailedAsync(server, RemoteFailure.InvalidResponse, cancellationToken).ConfigureAwait(false);
        }
    }

    // After every sync, also a failed one: what one server sent is still good, and removals check each server by itself.
    private async Task<Materialized> MaterializeAsync(bool verifyEveryStub, CancellationToken cancellationToken)
    {
        if (_materializer is null)
        {
            return new Materialized(null, false);
        }

        var materialized = new Materialized(null, true);
        try
        {
            materialized = new Materialized(await _materializer.MaterializeAsync(verifyEveryStub, cancellationToken).ConfigureAwait(false), false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a remote address.
            _logger.LogError("Could not materialize the federated folder: {Error}", exception.GetType().Name);
        }

        if (_linker is null)
        {
            return materialized;
        }

        try
        {
            // The nightly run also finds by path a stub whose item is not where its path says.
            await _linker.LinkAsync(verifyEveryStub, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError("Could not link the versions of the titles: {Error}", exception.GetType().Name);
        }

        return materialized;
    }

    // False when the check failed, so that it counts as skipped. A cancelled one throws and counts as skipped too.
    private async Task<bool> FollowServerLibrariesAsync(CancellationToken cancellationToken)
    {
        if (_libraries is null)
        {
            return true;
        }

        try
        {
            await _libraries.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError("Could not follow the libraries of the servers: {Error}", exception.GetType().Name);
            return false;
        }
    }

    // A sync that never completes the library check counts as a skipped one (decisions 173, 185).
    private void MarkLibraryFollowSkipped()
    {
        try
        {
            _libraries?.MarkFollowSkipped();
        }
        catch (Exception exception)
        {
            _logger.LogError("Could not mark the check of the server libraries as skipped: {Error}", exception.GetType().Name);
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

        // Before the read, so that what changes during it comes up in the next one.
        string? cursor = null;
        if (server.Mode == RemoteMode.JellyMesh)
        {
            var position = await _reader.GetFeedPositionAsync(server, cancellationToken).ConfigureAwait(false);
            if (!position.IsSuccess)
            {
                await MarkFailedAsync(server, position.Failure, cancellationToken).ConfigureAwait(false);
                return;
            }

            cursor = position.Value;
        }

        var read = await _reader.ReadAsync(server, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            await MarkFailedAsync(server, read.Failure, cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("Read {Count} catalog entries from remote server {ServerName}", read.Value!.Count, server.Name);
        _runReads[server.Id] = read.Value.Count;
        var entries = await WithCollectionsAsync(server, read.Value, cancellationToken).ConfigureAwait(false);
        var mark = new SyncMark(started.Value, server.ImportedLibraryIds);
        var saved = await _index.ReplaceServerAsync(server.Id, entries, _timeProvider.GetUtcNow(), mark, cancellationToken).ConfigureAwait(false);
        TrackSave(saved);
        RememberCursor(server.Id, saved ? cursor : null);
    }

    private async Task SyncChangesAsync(RemoteServerInfo server, CancellationToken cancellationToken)
    {
        var catalog = _index.Snapshot.Find(server.Id);
        if (catalog is not { State: ServerSyncState.Complete, Mark: { } mark } || !CoversSameLibraries(mark, server))
        {
            await SyncServerAsync(server, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (server.Mode == RemoteMode.JellyMesh)
        {
            await SyncFeedAsync(server, catalog, cancellationToken).ConfigureAwait(false);
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
        _runReads[server.Id] = read.Value.Count;
        var next = new SyncMark(started.Value, server.ImportedLibraryIds);
        TrackSave(await _index.MergeServerAsync(server.Id, KeepCollections(read.Value, catalog), _timeProvider.GetUtcNow(), next, cancellationToken).ConfigureAwait(false));
    }

    // The change feed instead of polling. The mark still moves: if the remote stops being a JellyMesh one, polling goes on
    // from here. Without a cursor, as after a restart of this server, only a full read can tell where things stand.
    private async Task SyncFeedAsync(RemoteServerInfo server, ServerCatalog catalog, CancellationToken cancellationToken)
    {
        if (!_feedCursors.TryGetValue(server.Id, out var cursor))
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
        var read = await _reader.ReadFeedAsync(server, cursor, known, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            await MarkFailedAsync(server, read.Failure, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (read.Value!.Reset)
        {
            _logger.LogInformation("The change feed of remote server {ServerName} cannot be continued, reading it all", server.Name);
            await SyncServerAsync(server, cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("Read {Count} entries from the change feed of remote server {ServerName}", read.Value.Items.Count, server.Name);
        _runReads[server.Id] = read.Value.Items.Count;
        var next = new SyncMark(started.Value, server.ImportedLibraryIds);
        var saved = await _index.MergeServerAsync(server.Id, KeepCollections(read.Value.Items, catalog), _timeProvider.GetUtcNow(), next, cancellationToken).ConfigureAwait(false);
        TrackSave(saved);

        // Not saved means not applied, so the old cursor reads the same changes again.
        if (saved)
        {
            RememberCursor(server.Id, read.Value.Cursor);
        }
    }

    // A read of changes does not read collections, and a film does not change when its collection does (decision 52).
    private static IReadOnlyList<IndexedItem> KeepCollections(IReadOnlyList<IndexedItem> changed, ServerCatalog catalog)
    {
        var known = catalog.Items.Where(item => item.Collection is not null).ToDictionary(item => item.ItemId, item => item.Collection, StringComparer.Ordinal);
        return known.Count == 0
            ? changed
            : changed.Select(item => known.TryGetValue(item.ItemId, out var collection) ? item with { Collection = collection } : item).ToList();
    }

    // Only with the option on: a stock server by the stock route, a JellyMesh one by the ProviderApi. A failed read keeps
    // what was known, so that the .nfo does not lose its collection for one night.
    private async Task<IReadOnlyList<IndexedItem>> WithCollectionsAsync(RemoteServerInfo server, IReadOnlyList<IndexedItem> entries, CancellationToken cancellationToken)
    {
        if (_store?.Read(config => config.FederateCollections) != true)
        {
            return entries;
        }

        var films = entries.Where(item => item.Kind == IndexedItemKind.Movie).ToList();
        var libraries = films.Select(item => item.LibraryId).Distinct(StringComparer.Ordinal).ToList();
        if (libraries.Count == 0)
        {
            return entries;
        }

        var filmIds = films.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
        var read = await _reader.ReadCollectionsAsync(server, libraries, filmIds, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            _logger.LogWarning("Could not read the collections of remote server {ServerName}: {Failure}. The ones known are kept", server.Name, read.Failure);
            return _index.Snapshot.Find(server.Id) is { } catalog ? KeepCollections(entries, catalog) : entries;
        }

        return entries
            .Select(item => item.Kind == IndexedItemKind.Movie && read.Value!.TryGetValue(item.ItemId, out var collection) ? item with { Collection = collection } : item)
            .ToList();
    }

    private void RememberCursor(Guid serverId, string? cursor)
    {
        if (cursor is null)
        {
            _feedCursors.TryRemove(serverId, out _);
        }
        else
        {
            _feedCursors[serverId] = cursor;
        }
    }

    private async Task<RemoteResult<DateTimeOffset>> ReadClockAsync(RemoteServerInfo server, CancellationToken cancellationToken)
    {
        var clock = await _reader.GetServerTimeAsync(server, cancellationToken).ConfigureAwait(false);
        return clock.IsSuccess
            ? RemoteResult.Success(clock.Value ?? _timeProvider.GetUtcNow())
            : RemoteResult.Fail<DateTimeOffset>(clock.Failure);
    }

    // A server already marked failed in the index, also before a restart, is not written to the activity log again.
    private async Task MarkFailedAsync(RemoteServerInfo server, RemoteFailure failure, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Could not read the catalog of remote server {ServerName}: {Failure}. Its entries are kept", server.Name, failure);
        var wasFailing = _index.Snapshot.Find(server.Id) is { State: ServerSyncState.Stale, LastFailure: not null };
        _runFailures[server.Id] = KeyValuePair.Create(server.Name, failure);
        TrackSave(await _index.MarkServerFailedAsync(server.Id, failure, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false));
        if (!wasFailing)
        {
            WriteActivity(ActivityEntries.SyncFailed(server.Name, failure));
        }
    }

    // A scheduled sync, before it claims the run, writes once each stored password that no longer decrypts (49). A missing
    // key is not that.
    private void ReportUnreadablePasswords()
    {
        if (_activity is null || _vault.GetKeyState() == KeyState.Missing)
        {
            return;
        }

        foreach (var server in _unreadablePasswords.Update(_registry.GetServers()))
        {
            WriteActivity(ActivityEntries.PasswordUnreadable(server.Name));
        }
    }

    private void WriteActivity(ActivityEntry entry) => _activity?.Write(entry);

    private void TrackSave(bool saved)
    {
        if (!saved)
        {
            _run = _run with { IndexSaveFailed = true };
        }
    }

    private sealed record RunState(bool IsRunning, bool IndexSaveFailed, DateTimeOffset? StartedUtc, DateTimeOffset? FinishedUtc);

    // The report of this run's materialization, null without one, and whether it threw.
    private sealed record Materialized(MaterializationReport? Report, bool Failed);
}
