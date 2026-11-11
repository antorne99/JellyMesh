using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// <see cref="ILastServedSourceStore"/> over a file in the plugin data folder. It holds the user, the stub and the server
/// GUID, never an address or a token. A write replaces the file in one step; a file that cannot be read is set aside.
/// </summary>
public sealed class LastServedSourceStore : ILastServedSourceStore, IDisposable
{
    /// <summary>
    /// The file name.
    /// </summary>
    public const string FileName = "last-served.json";

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
    private readonly ConcurrentDictionary<(Guid UserId, string StubId), Guid> _servers;

    /// <summary>
    /// Initializes a new instance of the <see cref="LastServedSourceStore"/> class.
    /// </summary>
    /// <param name="folder">The folder of the file.</param>
    /// <param name="logger">The logger.</param>
    public LastServedSourceStore(string folder, ILogger<LastServedSourceStore> logger)
    {
        _path = Path.Combine(folder, FileName);
        _logger = logger;
        _servers = new ConcurrentDictionary<(Guid UserId, string StubId), Guid>(
            Load().Select(entry => KeyValuePair.Create((entry.UserId, entry.StubId), entry.ServerId)));
    }

    /// <inheritdoc />
    public Guid? Find(Guid userId, string stubId)
    {
        return _servers.TryGetValue((userId, stubId), out var serverId) ? serverId : null;
    }

    /// <inheritdoc />
    public async Task RecordAsync(Guid userId, string stubId, Guid serverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stubId);

        var key = (userId, stubId);
        if (_servers.TryGetValue(key, out var known) && known == serverId)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _servers[key] = serverId;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Cannot save the last server that served a title: {Error}", exception.GetType().Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

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
            if (file?.Version == CurrentVersion)
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
