using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// <see cref="ISyncHistory"/> over a file in the plugin data folder. A write replaces the file in one step. A file that
/// cannot be read is set aside and the history starts empty.
/// </summary>
public sealed class SyncHistoryStore : ISyncHistory, IDisposable
{
    /// <summary>
    /// The file name.
    /// </summary>
    public const string FileName = "sync-history.json";

    /// <summary>
    /// How many runs are kept.
    /// </summary>
    public const int Capacity = 50;

    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions _options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly ILogger<SyncHistoryStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IReadOnlyList<SyncRun> _runs;
    private volatile bool _isDamaged;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncHistoryStore"/> class.
    /// </summary>
    /// <param name="folder">The folder of the file.</param>
    /// <param name="logger">The logger.</param>
    public SyncHistoryStore(string folder, ILogger<SyncHistoryStore> logger)
    {
        _path = Path.Combine(folder, FileName);
        _logger = logger;
        _runs = Load();
    }

    /// <inheritdoc />
    public IReadOnlyList<SyncRun> Runs => _runs;

    /// <inheritdoc />
    public bool IsDamaged => _isDamaged;

    /// <inheritdoc />
    public async Task AddAsync(SyncRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = _runs.Prepend(run).Take(Capacity).ToList();
            _runs = next;
            await SaveAsync(next, cancellationToken).ConfigureAwait(false);
            _isDamaged = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task SaveAsync(IReadOnlyList<SyncRun> runs, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var temporary = _path + ".tmp";
        try
        {
            var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, new HistoryFile(CurrentVersion, runs), _options, cancellationToken).ConfigureAwait(false);
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

    private IReadOnlyList<SyncRun> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(_path);
            var file = JsonSerializer.Deserialize<HistoryFile>(stream, _options);
            if (file?.Version == CurrentVersion && IsWhole(file.Runs))
            {
                return file.Runs.Take(Capacity).ToList();
            }

            _logger.LogError("The file of the sync history has an unsupported format. The history starts empty");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError("Cannot read the file of the sync history: {Error}. The history starts empty", exception.GetType().Name);
        }

        _isDamaged = true;
        KeepDamagedCopy();
        return [];
    }

    // Tells whether no run and no server row is null, which the serializer lets through in lists.
    private static bool IsWhole(IReadOnlyList<SyncRun?> runs) =>
        runs.All(run => run is not null && run.Servers is not null && run.Servers.All(server => server is not null));

    private void KeepDamagedCopy()
    {
        try
        {
            File.Copy(_path, _path + ".damaged", overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Cannot keep a copy of the damaged file of the sync history: {Error}", exception.GetType().Name);
        }
    }

    private sealed record HistoryFile(int Version, IReadOnlyList<SyncRun> Runs);
}
