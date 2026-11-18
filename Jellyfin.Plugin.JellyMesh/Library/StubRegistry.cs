using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="IStubRegistry"/> in memory, with the tables of the plugin database as its durable copy. A change is
/// written first, in one transaction, and only then made visible. If the rows are lost the stubs are minted again on
/// the same paths, so the Jellyfin items and their watch state stay.
/// </summary>
public sealed class StubRegistry : IStubRegistry, IDisposable
{
    private readonly IPluginDatabase _database;
    private readonly IPluginConfigurationStore _store;
    private readonly ILogger<StubRegistry> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Lookup _lookup;
    private long _generation;

    // False while the database could not be read: memory is then not its copy, and nothing is saved over it.
    private bool _loaded;

    // What the tables hold, to write only what changed; null when that is not known, so the next save writes it all.
    private StubState? _stored;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubRegistry"/> class, reading the registry from the database.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="logger">The logger.</param>
    public StubRegistry(IPluginDatabase database, IPluginConfigurationStore store, ILogger<StubRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
        _store = store;
        _logger = logger;

        // Built once at startup, before any request; the read runs on a pool thread of its own, so the wait cannot deadlock.
        _lookup = new Lookup(LoadAsync(CancellationToken.None).GetAwaiter().GetResult() ?? StubState.Empty);
    }

    /// <summary>
    /// Gets the current state. The copy never changes.
    /// </summary>
    public StubState State => _lookup.State;

    /// <summary>
    /// Gets how many saves succeeded since the start, so that a reader can tell whether the state changed since it looked.
    /// </summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <inheritdoc />
    public StubEntry? Find(string stubId) => _lookup.ById.TryGetValue(stubId, out var stub) ? stub : null;

    /// <inheritdoc />
    public VersionGroup? FindGroup(string stubId) => _lookup.GroupsByStub.TryGetValue(stubId, out var group) ? group : null;

    /// <inheritdoc />
    public VersionGroup? FindGroupOfLocal(Guid itemId) => _lookup.GroupsByLocal.TryGetValue(itemId, out var group) ? group : null;

    /// <inheritdoc />
    public bool HasTitlesOf(Guid serverId)
    {
        var state = _lookup.State;
        return state.Stubs.Any(stub => stub.ServerId == serverId) || state.Series.Any(series => series.ServerId == serverId);
    }

    /// <summary>
    /// Finds the episode stubs of a remote series on one server, through the series stubs it is a source of (decision 51).
    /// </summary>
    /// <param name="serverId">The remote server.</param>
    /// <param name="seriesItemId">The series item on that server.</param>
    /// <returns>The episode stubs, empty if the series has none.</returns>
    public IReadOnlyList<StubEntry> FindEpisodesOfSeries(Guid serverId, string seriesItemId)
    {
        var state = _lookup.State;
        var series = state.Series
            .Where(entry => entry.ServerId == serverId && entry.Sources.Any(source => source.ServerId == serverId
                && string.Equals(source.ItemId, seriesItemId, StringComparison.OrdinalIgnoreCase)))
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);
        return state.Stubs
            .Where(stub => stub.Kind == StubKind.Episode && stub.ServerId == serverId && stub.SeriesStubId is { } id && series.Contains(id))
            .ToList();
    }

    /// <inheritdoc />
    public StubEntry? FindByPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var folder = _store.Read(config => config.FederatedFolderPath);
        var relative = FederatedPaths.RelativeTo(folder, path);
        return relative is not null && _lookup.ByPath.TryGetValue(relative, out var stub) ? stub : null;
    }

    /// <summary>
    /// Takes the lock that materialization and cleanup share: both rewrite the state and the folder. A registry that could
    /// not be read is read again first, so the holder starts from what the database holds.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="IOException">The registry still cannot be read; the lock is not taken, so no one acts on an empty state.</exception>
    public async Task EnterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_loaded)
        {
            return;
        }

        StubState? state;
        try
        {
            state = await LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _gate.Release();
            throw;
        }

        if (state is null)
        {
            _gate.Release();
            throw new IOException("The stub registry cannot be read from the database.");
        }

        _lookup = new Lookup(state);
        Interlocked.Increment(ref _generation);
    }

    /// <summary>
    /// Releases the lock.
    /// </summary>
    public void Exit() => _gate.Release();

    /// <summary>
    /// Saves a state and makes it visible. The caller holds the lock.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="IOException">The database could not be read or written. The current state stays.</exception>
    public async Task SaveAsync(StubState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!_loaded)
        {
            throw new IOException("The stub registry could not be read from the database, so it is not saved over.");
        }

        // A state equal to what the tables hold writes nothing, but still counts as a save.
        if (StubTables.Plan(_stored, state) is { } write)
        {
            try
            {
                await _database.WriteAsync((connection, transaction) => StubTables.Write(connection, transaction, write), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is StoreSuspendedException or SqliteException)
            {
                // A store that comes back may not hold what it held, so it is read again at the next lock.
                if (exception is StoreSuspendedException)
                {
                    (_loaded, _stored) = (false, null);
                }

                throw new IOException("The stub registry could not be saved: " + exception.GetType().Name, exception);
            }
        }

        _stored = state;
        _lookup = new Lookup(state);
        Interlocked.Increment(ref _generation);
    }

    /// <summary>
    /// Records a server as removed by the administrator, then removes it, under the lock: a run in progress would otherwise
    /// save over the mark. If the removal fails or finds no server, the mark this call added is taken back.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="remove">What takes the server out of the configuration. False when there was no such server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the server was removed.</returns>
    /// <exception cref="Exception">The mark or the removal could not be saved. The server stays.</exception>
    public async Task<bool> RemoveServerAsync(Guid serverId, Func<Task<bool>> remove, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = State;
            var marked = !state.RemovedServers.Contains(serverId);
            if (marked)
            {
                await SaveAsync(state with { RemovedServers = [.. state.RemovedServers, serverId] }, cancellationToken).ConfigureAwait(false);
            }

            var removed = false;
            try
            {
                removed = await remove().ConfigureAwait(false);
                return removed;
            }
            finally
            {
                if (marked && !removed)
                {
                    await UnmarkAsync(serverId).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Records as removed every server that the stubs or the index still name but the settings do not, under the lock, so
    /// that the cleanup after a removal deletes what they left (decision 44). A server configured again wins over its mark.
    /// </summary>
    /// <param name="configured">Reads the servers in the settings. It runs under the lock, so a removal in progress is never seen half done.</param>
    /// <param name="known">Other servers that left content, such as those with a catalog in the index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many servers were marked now.</returns>
    /// <exception cref="IOException">The marks could not be saved. Nothing is marked.</exception>
    public async Task<int> ForgetServersAsync(Func<IReadOnlySet<Guid>> configured, IEnumerable<Guid> known, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(known);

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = State;
            var settings = configured();
            var missing = state.Stubs.SelectMany(stub => stub.Sources)
                .Concat(state.Series.SelectMany(series => series.Sources))
                .Select(source => source.ServerId)
                .Concat(known)
                .Where(server => !settings.Contains(server) && !state.RemovedServers.Contains(server))
                .Distinct()
                .ToList();
            if (missing.Count > 0)
            {
                await SaveAsync(state with { RemovedServers = [.. state.RemovedServers, .. missing] }, cancellationToken).ConfigureAwait(false);
            }

            return missing.Count;
        }
        finally
        {
            Exit();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    // Takes back a mark whose server stays. If that cannot be saved either, the mark stays: the server is still configured.
    private async Task UnmarkAsync(Guid serverId)
    {
        var state = State;
        try
        {
            await SaveAsync(state with { RemovedServers = state.RemovedServers.Where(server => server != serverId).ToList() }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError("Cannot take back the removal mark of a server that stays: {Error}", exception.GetType().Name);
        }
    }

    // Null when the database is suspended or cannot be read, to be tried again at the next lock. Rows that do not read give
    // an empty registry, whose stubs are minted again and written whole at the next save.
    private async Task<StubState?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = await _database.ReadAsync(StubTables.Load, cancellationToken).ConfigureAwait(false);
            (_loaded, _stored) = (true, state);
            return state;
        }
        catch (Exception exception) when (exception is StoreSuspendedException or SqliteException)
        {
            _logger.LogError("Cannot read the stub registry from the database: {Error}. It is empty until the database can be read", exception.GetType().Name);
            return null;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidCastException)
        {
            _logger.LogError("The stub registry in the database is damaged: {Error}. Stubs are minted again at the next sync", exception.GetType().Name);
            (_loaded, _stored) = (true, null);
            return StubState.Empty;
        }
    }

    private sealed class Lookup
    {
        public Lookup(StubState state)
        {
            State = state;
            ById = state.Stubs.ToFrozenDictionary(stub => stub.Id, StringComparer.Ordinal);

            // The old path of a renamed stub finds it too, until its old item is gone; a current path always wins.
            var byPath = new Dictionary<string, StubEntry>(StringComparer.Ordinal);
            foreach (var stub in state.Stubs)
            {
                byPath.TryAdd(stub.RelativePath, stub);
            }

            foreach (var rename in state.Renames)
            {
                if (ById.TryGetValue(rename.StubId, out var stub))
                {
                    byPath.TryAdd(rename.OldPath, stub);
                }
            }

            ByPath = byPath.ToFrozenDictionary(StringComparer.Ordinal);

            // The first group wins for a member listed twice, which the planner never writes.
            var byStub = new Dictionary<string, VersionGroup>(StringComparer.Ordinal);
            var byLocal = new Dictionary<Guid, VersionGroup>();
            foreach (var group in state.Groups)
            {
                foreach (var stubId in group.StubIds)
                {
                    byStub.TryAdd(stubId, group);
                }

                foreach (var itemId in group.LocalItemIds)
                {
                    byLocal.TryAdd(itemId, group);
                }
            }

            GroupsByStub = byStub.ToFrozenDictionary(StringComparer.Ordinal);
            GroupsByLocal = byLocal.ToFrozenDictionary();
        }

        public StubState State { get; }

        public FrozenDictionary<string, StubEntry> ById { get; }

        public FrozenDictionary<string, StubEntry> ByPath { get; }

        public FrozenDictionary<string, VersionGroup> GroupsByStub { get; }

        public FrozenDictionary<Guid, VersionGroup> GroupsByLocal { get; }
    }
}
