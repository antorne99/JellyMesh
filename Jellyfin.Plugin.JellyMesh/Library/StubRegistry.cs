using System;
using System.Collections.Frozen;
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
    /// Records a server the administrator removed, under the lock: a run in progress would otherwise save over it.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task MarkRemovedAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = State;
            if (!state.RemovedServers.Contains(serverId))
            {
                await SaveAsync(state with { RemovedServers = [.. state.RemovedServers, serverId] }, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

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
        }

        public StubState State { get; }

        public FrozenDictionary<string, StubEntry> ById { get; }

        public FrozenDictionary<string, StubEntry> ByPath { get; }
    }
}
