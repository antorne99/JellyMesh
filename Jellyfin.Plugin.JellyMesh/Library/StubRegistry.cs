using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="IStubRegistry"/> over a file in the plugin data folder. A change is saved first and only then made visible,
/// and a write replaces the file in one step. If the file is lost the stubs are minted again on the same paths, so the
/// Jellyfin items and their watch state stay.
/// </summary>
public sealed class StubRegistry : IStubRegistry, IDisposable
{
    /// <summary>The file name.</summary>
    public const string FileName = "federated-stubs.json";

    private static readonly JsonSerializerOptions _options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly IPluginConfigurationStore _store;
    private readonly ILogger<StubRegistry> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Lookup _lookup;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubRegistry"/> class.
    /// </summary>
    /// <param name="folder">The folder of the file.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="logger">The logger.</param>
    public StubRegistry(string folder, IPluginConfigurationStore store, ILogger<StubRegistry> logger)
    {
        _path = Path.Combine(folder, FileName);
        _store = store;
        _logger = logger;
        _lookup = new Lookup(Load());
    }

    /// <summary>
    /// Gets the current state. The copy never changes.
    /// </summary>
    public StubState State => _lookup.State;

    /// <inheritdoc />
    public StubEntry? Find(string stubId) => _lookup.ById.TryGetValue(stubId, out var stub) ? stub : null;

    /// <inheritdoc />
    public VersionGroup? FindGroup(string stubId) => _lookup.GroupsByStub.TryGetValue(stubId, out var group) ? group : null;

    /// <inheritdoc />
    public VersionGroup? FindGroupOfLocal(Guid itemId) => _lookup.GroupsByLocal.TryGetValue(itemId, out var group) ? group : null;

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
    /// Takes the lock that materialization and cleanup share: both rewrite the state and the folder.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task EnterAsync(CancellationToken cancellationToken) => _gate.WaitAsync(cancellationToken);

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
    /// <exception cref="IOException">The file could not be written. The current state stays.</exception>
    public async Task SaveAsync(StubState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var temporary = _path + ".tmp";
        try
        {
            var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, state, _options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                RandomAccess.FlushToDisk(stream.SafeFileHandle);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }

        _lookup = new Lookup(state);
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

    private StubState Load()
    {
        if (!File.Exists(_path))
        {
            return StubState.Empty;
        }

        try
        {
            using var stream = File.OpenRead(_path);
            var state = JsonSerializer.Deserialize<StubState>(stream, _options);
            if (state?.Version == StubState.CurrentVersion)
            {
                return state;
            }

            if (state?.Version == StubState.FlatLayoutVersion)
            {
                return state with { Version = StubState.CurrentVersion };
            }

            _logger.LogError("The stub registry has an unsupported format. Stubs are minted again at the next sync");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError("Cannot read the stub registry: {Error}. Stubs are minted again at the next sync", exception.GetType().Name);
        }

        return StubState.Empty;
    }

    private sealed class Lookup
    {
        public Lookup(StubState state)
        {
            State = state;
            ById = state.Stubs.ToFrozenDictionary(stub => stub.Id, StringComparer.Ordinal);
            ByPath = state.Stubs.ToFrozenDictionary(stub => stub.RelativePath, StringComparer.Ordinal);

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
