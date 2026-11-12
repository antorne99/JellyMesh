using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="IWatchStateCarryLog"/> over a file in the plugin data folder. It holds user and item IDs and a date. A write
/// replaces the file in one step; a file that cannot be read is set aside.
/// </summary>
public sealed class WatchStateCarryLog : IWatchStateCarryLog, IDisposable
{
    /// <summary>
    /// The file name.
    /// </summary>
    public const string FileName = "watch-state-carries.json";

    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions _options = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly ILogger<WatchStateCarryLog> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId), DateTime?> _carries = new();
    private volatile bool _unsaved;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchStateCarryLog"/> class.
    /// </summary>
    /// <param name="folder">The folder of the file.</param>
    /// <param name="logger">The logger.</param>
    public WatchStateCarryLog(string folder, ILogger<WatchStateCarryLog> logger)
    {
        _path = Path.Combine(folder, FileName);
        _logger = logger;
        foreach (var entry in Load())
        {
            _carries[(entry.UserId, entry.ItemId)] = entry.LastPlayedDate;
        }
    }

    /// <inheritdoc />
    public bool TryFind(Guid userId, Guid fromItemId, out DateTime? lastPlayedDate) => _carries.TryGetValue((userId, fromItemId), out lastPlayedDate);

    /// <inheritdoc />
    public void Record(Guid userId, Guid fromItemId, DateTime? lastPlayedDate)
    {
        _carries[(userId, fromItemId)] = lastPlayedDate;
        _unsaved = true;
    }

    /// <inheritdoc />
    public async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_unsaved)
            {
                return true;
            }

            _unsaved = false;
            await WriteAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _unsaved = true;
            _logger.LogError("Cannot save the watch state carries done: {Error}", exception.GetType().Name);
            return false;
        }
        catch
        {
            _unsaved = true;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task WriteAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var entries = _carries
            .Select(pair => new Entry(pair.Key.UserId, pair.Key.ItemId, pair.Value))
            .OrderBy(entry => entry.UserId)
            .ThenBy(entry => entry.ItemId)
            .ToList();
        var temporary = _path + ".tmp";
        try
        {
            var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, new CarryFile(CurrentVersion, entries), _options, cancellationToken).ConfigureAwait(false);
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
            var file = JsonSerializer.Deserialize<CarryFile>(stream, _options);
            if (file?.Version == CurrentVersion && file.Entries.All(entry => entry is not null))
            {
                return file.Entries;
            }

            _logger.LogError("The file of the watch state carries done has an unsupported format. It is set aside");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError("Cannot read the file of the watch state carries done: {Error}. It is set aside", exception.GetType().Name);
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
            _logger.LogError("Cannot keep a copy of the damaged file of the watch state carries: {Error}", exception.GetType().Name);
        }
    }

    private sealed record Entry(Guid UserId, Guid ItemId, DateTime? LastPlayedDate);

    private sealed record CarryFile(int Version, IReadOnlyList<Entry> Entries);
}
