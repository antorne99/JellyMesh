using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// <see cref="IManualMergeStore"/> over a file in the plugin data folder, with the index and the stub registry: these are
/// data about the catalog, not settings. A write replaces the file in one step. A file that cannot be read is set aside, and
/// the titles show apart again, which loses no watch state, until the administrator repeats the merges.
/// </summary>
public sealed class ManualMergeStore : IManualMergeStore, IDisposable
{
    /// <summary>
    /// The file name.
    /// </summary>
    public const string FileName = "manual-merges.json";

    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions _options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly ILogger<ManualMergeStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IReadOnlyList<ManualMerge> _merges;
    private volatile bool _isDamaged;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManualMergeStore"/> class.
    /// </summary>
    /// <param name="folder">The folder of the file.</param>
    /// <param name="logger">The logger.</param>
    public ManualMergeStore(string folder, ILogger<ManualMergeStore> logger)
    {
        _path = Path.Combine(folder, FileName);
        _logger = logger;
        _merges = Load();
    }

    /// <inheritdoc />
    public IReadOnlyList<ManualMerge> Merges => _merges;

    /// <inheritdoc />
    public bool IsDamaged => _isDamaged;

    /// <inheritdoc />
    public async Task UpdateAsync(Func<IReadOnlyList<ManualMerge>, IReadOnlyList<ManualMerge>> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = change(_merges);
            await SaveAsync(next, cancellationToken).ConfigureAwait(false);
            _merges = next;
            _isDamaged = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task SaveAsync(IReadOnlyList<ManualMerge> merges, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var temporary = _path + ".tmp";
        try
        {
            var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, new MergeFile(CurrentVersion, merges), _options, cancellationToken).ConfigureAwait(false);
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

    private IReadOnlyList<ManualMerge> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(_path);
            var file = JsonSerializer.Deserialize<MergeFile>(stream, _options);
            if (file?.Version == CurrentVersion)
            {
                return file.Merges;
            }

            _logger.LogError("The file of manual merges has an unsupported format. Its merges are not applied");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError("Cannot read the file of manual merges: {Error}. Its merges are not applied", exception.GetType().Name);
        }

        _isDamaged = true;
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
            _logger.LogError("Cannot keep a copy of the damaged file of manual merges: {Error}", exception.GetType().Name);
        }
    }

    private sealed record MergeFile(int Version, IReadOnlyList<ManualMerge> Merges);
}
