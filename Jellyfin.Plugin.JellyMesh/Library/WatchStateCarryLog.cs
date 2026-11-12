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
/// replaces the file in one step. A damaged file is set aside; one that cannot be read for now suspends the carries until
/// it reads again (decision 176).
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
    private readonly Func<string, Stream> _openRead;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId), DateTime?> _carries = new();
    private volatile bool _unsaved;
    private volatile bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchStateCarryLog"/> class.
    /// </summary>
    /// <param name="folder">The folder of the file.</param>
    /// <param name="logger">The logger.</param>
    public WatchStateCarryLog(string folder, ILogger<WatchStateCarryLog> logger)
        : this(folder, logger, File.OpenRead)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchStateCarryLog"/> class that reads the file through a test seam.
    /// </summary>
    /// <param name="folder">The folder of the file.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="openRead">Opens the file to read it.</param>
    internal WatchStateCarryLog(string folder, ILogger<WatchStateCarryLog> logger, Func<string, Stream> openRead)
    {
        _path = Path.Combine(folder, FileName);
        _logger = logger;
        _openRead = openRead;
        EnsureLoaded();
    }

    /// <inheritdoc />
    public bool EnsureLoaded()
    {
        if (_loaded)
        {
            return true;
        }

        _gate.Wait();
        try
        {
            if (!_loaded && Load() is { } entries)
            {
                foreach (var entry in entries)
                {
                    _carries[(entry.UserId, entry.ItemId)] = entry.LastPlayedDate;
                }

                _loaded = true;
            }

            return _loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public bool TryFind(Guid userId, Guid fromItemId, out DateTime? lastPlayedDate) => _carries.TryGetValue((userId, fromItemId), out lastPlayedDate);

    /// <inheritdoc />
    public void Record(Guid userId, Guid fromItemId, DateTime? lastPlayedDate)
    {
        if (!_loaded)
        {
            return;
        }

        _carries[(userId, fromItemId)] = lastPlayedDate;
        _unsaved = true;
    }

    /// <inheritdoc />
    public async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A file not read yet would be replaced by what little is in memory.
            if (!_loaded)
            {
                return false;
            }

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

    // Null when the file cannot be read for now: the carries wait for it rather than start again from nothing.
    private IReadOnlyList<Entry>? Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            using var stream = _openRead(_path);
            var file = JsonSerializer.Deserialize<CarryFile>(stream, _options);
            if (file?.Version == CurrentVersion && file.Entries.All(entry => entry is not null))
            {
                return file.Entries;
            }

            _logger.LogError("The file of the watch state carries done has an unsupported format. It is set aside");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            _logger.LogError("Cannot read the file of the watch state carries done: {Error}. It is set aside", exception.GetType().Name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Cannot read the file of the watch state carries done for now: {Error}. Carries wait for it", exception.GetType().Name);
            return null;
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
