using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// <see cref="ILastServedSourceStore"/> over a file in the plugin data folder. It holds the user, the stub and the server
/// GUID, never an address or a token. A change is written <see cref="SaveDelay"/> after it, and what is pending when the
/// server stops (Q76). A write replaces the file in one step; a file that cannot be read is set aside.
/// </summary>
public sealed class LastServedSourceStore : ILastServedSourceStore, IHostedService, IDisposable
{
    /// <summary>
    /// The file name.
    /// </summary>
    public const string FileName = "last-served.json";

    /// <summary>
    /// How long after a change the file is written. Changes in between go in the same write, and more changes do not put
    /// it off.
    /// </summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(30);

    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions _options = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly ILogger<LastServedSourceStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<(Guid UserId, string StubId), Guid> _servers = new();
    private readonly ITimer _timer;
    private volatile bool _unsaved;
    private volatile bool _disposed;
    private int _scheduled;

    /// <summary>
    /// Initializes a new instance of the <see cref="LastServedSourceStore"/> class.
    /// </summary>
    /// <param name="folder">The folder of the file.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock of the deferred write.</param>
    public LastServedSourceStore(string folder, ILogger<LastServedSourceStore> logger, TimeProvider? timeProvider = null)
    {
        _path = Path.Combine(folder, FileName);
        _logger = logger;
        foreach (var entry in Load())
        {
            _servers[(entry.UserId, entry.StubId)] = entry.ServerId;
        }

        _timer = (timeProvider ?? TimeProvider.System).CreateTimer(_ => _ = SaveLaterAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public Guid? Find(Guid userId, string stubId)
    {
        return _servers.TryGetValue((userId, stubId), out var serverId) ? serverId : null;
    }

    /// <inheritdoc />
    public Task RecordAsync(Guid userId, string stubId, Guid serverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stubId);

        var key = (userId, stubId);
        if (_unsaved || !_servers.TryGetValue(key, out var known) || known != serverId)
        {
            _servers[key] = serverId;
            Changed();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public int Prune(Func<string, bool> stubExists, IReadOnlySet<Guid> removedServers)
    {
        ArgumentNullException.ThrowIfNull(stubExists);
        ArgumentNullException.ThrowIfNull(removedServers);

        var dropped = 0;
        foreach (var (key, serverId) in _servers)
        {
            if ((removedServers.Contains(serverId) || !stubExists(key.StubId)) && _servers.TryRemove(new KeyValuePair<(Guid, string), Guid>(key, serverId)))
            {
                dropped++;
            }
        }

        if (dropped > 0)
        {
            Changed();
        }

        return dropped;
    }

    /// <summary>
    /// Writes the file now if something changed since the last write.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_unsaved)
            {
                return;
            }

            // Cleared first: a change during the write is written by the next one.
            _unsaved = false;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _unsaved = true;
            _logger.LogWarning("Cannot save the last server that served a title: {Error}", exception.GetType().Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => FlushAsync(cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Dispose();
        FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        _disposed = true;
        _gate.Dispose();
    }

    // Starts the deferred write unless one is waiting already.
    private void Changed()
    {
        _unsaved = true;
        if (!_disposed && Interlocked.Exchange(ref _scheduled, 1) == 0)
        {
            _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task SaveLaterAsync()
    {
        Interlocked.Exchange(ref _scheduled, 0);
        if (_disposed)
        {
            return;
        }

        try
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Closed meanwhile: the close wrote it.
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var entries = _servers
            .Select(pair => new Entry(pair.Key.UserId, pair.Key.StubId, pair.Value))
            .OrderBy(entry => entry.UserId)
            .ThenBy(entry => entry.StubId, StringComparer.Ordinal)
            .ToList();
        var temporary = _path + ".tmp";
        try
        {
            var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, new LastServedFile(CurrentVersion, entries), _options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                RandomAccess.FlushToDisk(stream.SafeFileHandle);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private IReadOnlyList<Entry> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(_path);
            var file = JsonSerializer.Deserialize<LastServedFile>(stream, _options);
            if (file?.Version == CurrentVersion && file.Entries.All(entry => entry is not null))
            {
                return file.Entries;
            }

            _logger.LogError("The file of the last servers that served each title has an unsupported format. It is set aside");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError("Cannot read the file of the last servers that served each title: {Error}. It is set aside", exception.GetType().Name);
        }

        KeepDamagedCopy();
        return [];
    }

    private void KeepDamagedCopy()
    {
        try
        {
            File.Copy(_path, _path + ".damaged", overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Cannot keep a copy of the damaged file of the last servers: {Error}", exception.GetType().Name);
        }
    }

    private sealed record Entry(Guid UserId, string StubId, Guid ServerId);

    private sealed record LastServedFile(int Version, IReadOnlyList<Entry> Entries);
}
