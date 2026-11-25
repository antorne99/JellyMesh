using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Remote;
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
    private readonly ErrorLogLimits _errorLogs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Lookup _lookup;
    private long _generation;

    // False while the database could not be read: memory is then not its copy, and nothing is saved over it.
    private volatile bool _loaded;

    // What the tables hold, to write only what changed; null when that is not known, so the next save writes it all.
    private StubState? _stored;

    // The database generation the state was read in; another one means it may be stale.
    private long _readGeneration;

    // The last database generation a lookup asked to read the state again for.
    private long _askedGeneration;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubRegistry"/> class, reading the registry from the database.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="errorLogs">The limits of the errors in the log.</param>
    public StubRegistry(IPluginDatabase database, IPluginConfigurationStore store, ILogger<StubRegistry> logger, ErrorLogLimits? errorLogs = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        _errorLogs = errorLogs ?? new ErrorLogLimits();
        _database = database;
        _store = store;
        _logger = logger;

        // Built once at startup, before any request; the read runs on a pool thread of its own, so the wait cannot deadlock.
        _lookup = new Lookup(StubState.Empty, titlesKnown: false);
        if (LoadAsync(CancellationToken.None).GetAwaiter().GetResult() is { } read)
        {
            Publish(read);
        }
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
    public bool IsUnread => !_loaded || _database.Generation != Interlocked.Read(ref _readGeneration);

    /// <inheritdoc />
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (!IsUnread)
        {
            return;
        }

        try
        {
            await EnterAsync(cancellationToken).ConfigureAwait(false);
            Exit();
        }
        catch (IOException)
        {
            // Logged where it was read.
        }
    }

    /// <inheritdoc />
    public StubEntry? Find(string stubId) => Current().ById.TryGetValue(stubId, out var stub) ? stub : null;

    /// <inheritdoc />
    public VersionGroup? FindGroup(string stubId) => Current().GroupsByStub.TryGetValue(stubId, out var group) ? group : null;

    /// <inheritdoc />
    public VersionGroup? FindGroupOfLocal(Guid itemId) => Current().GroupsByLocal.TryGetValue(itemId, out var group) ? group : null;

    /// <inheritdoc />
    public bool HasTitlesOf(Guid serverId)
    {
        if (Known() is not { State: var state })
        {
            return true;
        }

        return state.Stubs.Any(stub => stub.ServerId == serverId) || state.Series.Any(series => series.ServerId == serverId);
    }

    /// <inheritdoc />
    public IReadOnlyList<string>? TypeFoldersOf(Guid serverId, TypeFolder type)
    {
        return Known() is { State: var state } ? StubLayout.TypeFoldersOf(state, serverId, type) : null;
    }

    /// <inheritdoc />
    public bool? UsesOlderLayout()
    {
        if (Known() is not { State: var state })
        {
            return null;
        }

        return StubLayout.IsLegacy(state)
            || (state.Stubs.Count == 0 && state.Series.Count == 0
                && _store.ReadOr(config => config.FederatedFolderPath, null) is { Length: > 0 } root
                && StubLayout.HasOlderStubFiles(root));
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

        // Every new item of every library comes here: while the folder is not known, no path is a stub's.
        if (_store.ReadOr(config => config.FederatedFolderPath, null) is not { } folder)
        {
            return null;
        }

        var relative = FederatedPaths.RelativeTo(folder, path);
        return relative is not null && Current().ByPath.TryGetValue(relative, out var stub) ? stub : null;
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
        if (_loaded && _database.Generation == _readGeneration)
        {
            return;
        }

        LoadedState? read;
        try
        {
            read = await LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _gate.Release();
            throw;
        }

        if (read is null)
        {
            _gate.Release();
            throw new IOException("The stub registry cannot be read from the database.");
        }

        Publish(read);
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
    public Task SaveAsync(StubState state, CancellationToken cancellationToken) => SaveAsync(state, false, cancellationToken);

    /// <summary>
    /// Saves the state a full materialization ended with, which holds every title it wrote, so that the registry can tell
    /// again that a server has none after rows that did not read. The caller holds the lock.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="IOException">The database could not be read or written. The current state stays.</exception>
    public Task SaveMaterializedAsync(StubState state, CancellationToken cancellationToken) => SaveAsync(state, true, cancellationToken);

    private async Task SaveAsync(StubState state, bool materialized, CancellationToken cancellationToken)
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
                var generation = _readGeneration;
                await _database.WriteAsync(
                    (connection, transaction) =>
                    {
                        // A database opened again since the state was read may not hold what the plan is based on.
                        if (_database.Generation != generation)
                        {
                            throw new StoreSuspendedException("The database was opened again since the stub registry was read.");
                        }

                        StubTables.Write(connection, transaction, write);
                    },
                    cancellationToken).ConfigureAwait(false);
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
        _lookup = new Lookup(state, materialized || _lookup.TitlesKnown);
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
            _logger.LogError("Cannot take back the removal mark of a server that stays: {Error}{Detail:l}", exception.GetType().Name, _errorLogs.Local.Of(exception));
        }
    }

    // Null when the database is suspended or cannot be read, to be tried again at the next lock. Rows that do not read give
    // an empty registry, whose stubs are minted again and written whole at the next save.
    private async Task<LoadedState?> LoadAsync(CancellationToken cancellationToken)
    {
        var generation = _readGeneration;
        try
        {
            var state = await _database.ReadAsync(
                (connection, transaction) =>
                {
                    generation = _database.Generation;
                    return StubTables.Load(connection, transaction);
                },
                cancellationToken).ConfigureAwait(false);
            return new LoadedState(state, state, generation);
        }
        catch (Exception exception) when (exception is StoreSuspendedException or SqliteException)
        {
            _logger.LogError("Cannot read the stub registry from the database: {Error}. It is empty until the database can be read{Detail:l}", exception.GetType().Name, _errorLogs.Local.Of(exception));
            return null;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidCastException or InvalidOperationException or OverflowException)
        {
            _logger.LogError("The stub registry in the database is damaged: {Error}. Stubs are minted again at the next sync{Detail:l}", exception.GetType().Name, _errorLogs.Local.Of(exception));
            return new LoadedState(StubState.Empty, null, generation);
        }
    }

    // Makes a loaded state visible, and only then counts it as read.
    private void Publish(LoadedState read)
    {
        _lookup = new Lookup(read.State, titlesKnown: read.Stored is not null);
        _stored = read.Stored;
        Interlocked.Exchange(ref _readGeneration, read.Generation);
        _loaded = true;
    }

    // The state in memory. When the database was opened again, it is read again in the background, once for each opening.
    private Lookup Current()
    {
        var generation = _database.Generation;
        if (generation != Interlocked.Read(ref _readGeneration) && Interlocked.Exchange(ref _askedGeneration, generation) != generation)
        {
            _ = Task.Run(ReloadInBackgroundAsync);
        }

        return _lookup;
    }

    // The state in memory when it holds every title of the database, null otherwise.
    private Lookup? Known() => !IsUnread && _lookup is { TitlesKnown: true } lookup ? lookup : null;

    private async Task ReloadInBackgroundAsync()
    {
        try
        {
            await ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError("Cannot read the stub registry again: {Error}{Detail:l}", exception.GetType().Name, _errorLogs.Local.Of(exception));
        }
    }

    // A state, what the tables hold if that is known, and the database generation it was read in.
    private sealed record LoadedState(StubState State, StubState? Stored, long Generation);

    private sealed class Lookup
    {
        public Lookup(StubState state, bool titlesKnown)
        {
            State = state;
            TitlesKnown = titlesKnown;
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

        // False while the state may lack titles still in the folder: read from rows that did not read, or not read at all.
        public bool TitlesKnown { get; }

        public FrozenDictionary<string, StubEntry> ById { get; }

        public FrozenDictionary<string, StubEntry> ByPath { get; }

        public FrozenDictionary<string, VersionGroup> GroupsByStub { get; }

        public FrozenDictionary<Guid, VersionGroup> GroupsByLocal { get; }
    }
}
