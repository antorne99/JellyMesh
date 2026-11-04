using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// Reads and writes the index file. A write replaces the file in one step, so a crash leaves the old file or the new
/// one and never half of either. The previous good file is kept as a backup.
/// </summary>
public sealed class IndexFile
{
    /// <summary>
    /// The file name.
    /// </summary>
    public const string FileName = "federation-index.json";

    private const int BufferSize = 64 * 1024;

    // The nullable and required checks make a file with missing parts fail to load, instead of loading with holes.
    private static readonly JsonSerializerOptions _options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    private readonly string _folder;
    private readonly string _path;
    private readonly string _backupPath;
    private readonly ILogger<IndexFile> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IndexFile"/> class.
    /// </summary>
    /// <param name="folder">The folder of the file.</param>
    /// <param name="logger">The logger.</param>
    public IndexFile(string folder, ILogger<IndexFile> logger)
    {
        _folder = folder;
        _path = Path.Combine(folder, FileName);
        _backupPath = _path + ".bak";
        _logger = logger;
    }

    /// <summary>
    /// Loads the index. Never throws for a bad file: a damaged one is set aside and reported in
    /// <see cref="IndexSnapshot.Health"/>, so that nobody takes the outcome for an index that is empty for good.
    /// </summary>
    /// <returns>The index.</returns>
    public IndexSnapshot Load()
    {
        if (!File.Exists(_path) && !File.Exists(_backupPath))
        {
            return IndexSnapshot.Empty(IndexHealth.Ok);
        }

        if (TryRead(_path) is { } current)
        {
            return current;
        }

        KeepDamagedCopy();

        if (TryRead(_backupPath) is { } backup)
        {
            _logger.LogError("The federation index is damaged. Loaded the previous copy: run a sync to bring it up to date");
            return backup with
            {
                Health = IndexHealth.RestoredFromBackup,
                Servers = backup.Servers.Select(server => server with { State = ServerSyncState.Stale }).ToList()
            };
        }

        _logger.LogError("The federation index is damaged and has no usable copy. It is empty until a sync fills it again");
        return IndexSnapshot.Empty(IndexHealth.Corrupt);
    }

    /// <summary>
    /// Saves the index.
    /// </summary>
    /// <param name="snapshot">The index.</param>
    /// <param name="keepCurrentAsBackup">Whether the file being replaced is good, so that it becomes the backup.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="IOException">The file could not be written. The current one is untouched.</exception>
    public async Task SaveAsync(IndexSnapshot snapshot, bool keepCurrentAsBackup, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_folder);

        var temporary = _path + ".tmp";
        try
        {
            var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, _options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                RandomAccess.FlushToDisk(stream.SafeFileHandle);
            }

            if (keepCurrentAsBackup && File.Exists(_path))
            {
                var temporaryBackup = _backupPath + ".tmp";
                File.Copy(_path, temporaryBackup, overwrite: true);
                File.Move(temporaryBackup, _backupPath, overwrite: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private IndexSnapshot? TryRead(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var snapshot = JsonSerializer.Deserialize<IndexSnapshot>(stream, _options);
            if (snapshot?.Version == IndexSnapshot.CurrentVersion)
            {
                return snapshot;
            }

            _logger.LogError("The federation index file {FileName} has an unsupported format", Path.GetFileName(path));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError("Cannot read the federation index file {FileName}: {Error}", Path.GetFileName(path), exception.GetType().Name);
        }

        return null;
    }

    private void KeepDamagedCopy()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            File.Copy(_path, _path + ".damaged", overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Cannot keep a copy of the damaged federation index: {Error}", exception.GetType().Name);
        }
    }
}
