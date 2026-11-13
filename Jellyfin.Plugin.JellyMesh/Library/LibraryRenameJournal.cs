using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The library renames whose user lists are not all rewritten yet, in an owner-only file of the plugin data folder that
/// a write replaces in one step (§10 row 290). It holds library names and ids only.
/// </summary>
public sealed class LibraryRenameJournal
{
    /// <summary>
    /// The file name.
    /// </summary>
    public const string FileName = "library-renames.json";

    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions _options = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly ILogger<LibraryRenameJournal> _logger;
    private readonly Lock _lock = new();
    private List<LibraryRenameRecord>? _records;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryRenameJournal"/> class.
    /// </summary>
    /// <param name="folder">The folder of the file.</param>
    /// <param name="logger">The logger.</param>
    public LibraryRenameJournal(string folder, ILogger<LibraryRenameJournal> logger)
    {
        _path = Path.Combine(folder, FileName);
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether a rename is not done, or the file cannot be read for now.
    /// </summary>
    public bool HasPending
    {
        get
        {
            lock (_lock)
            {
                return Load() is not { Count: 0 };
            }
        }
    }

    /// <summary>
    /// Gets the renames not done, empty while the file cannot be read.
    /// </summary>
    /// <returns>The records, oldest first.</returns>
    public IReadOnlyList<LibraryRenameRecord> Pending()
    {
        lock (_lock)
        {
            return Load()?.ToList() ?? [];
        }
    }

    /// <summary>
    /// Saves a rename before its folder moves.
    /// </summary>
    /// <param name="record">The rename.</param>
    /// <exception cref="IOException">The file cannot be read or written, so the rename must not start.</exception>
    public void Add(LibraryRenameRecord record)
    {
        lock (_lock)
        {
            var records = Load() ?? throw new IOException("The file of the library renames cannot be read.");
            Write([.. records, record]);
            _records = [.. records, record];
        }
    }

    /// <summary>
    /// Drops a rename once every user is rewritten.
    /// </summary>
    /// <param name="record">The rename.</param>
    /// <returns>False when the file cannot be written, so the rename is replayed later.</returns>
    public bool Remove(LibraryRenameRecord record)
    {
        lock (_lock)
        {
            if (Load() is not { } records)
            {
                return false;
            }

            var left = records.Where(other => other != record).ToList();
            try
            {
                Write(left);
                _records = left;
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogError("Cannot save the library renames: {Error}", exception.GetType().Name);
                return false;
            }
        }
    }

    /// <summary>
    /// Moves a rename the resume cannot trust to the damaged file, next to those set aside before, so it is never replayed
    /// (§10 row 329).
    /// </summary>
    /// <param name="record">The rename.</param>
    /// <returns>False when a file cannot be written, so the record stays and the caller leaves it alone.</returns>
    public bool SetAside(LibraryRenameRecord record)
    {
        lock (_lock)
        {
            if (Load() is not { } records)
            {
                return false;
            }

            var left = records.Where(other => other != record).ToList();
            try
            {
                var damaged = _path + ".damaged";
                Save(damaged, [.. SetAsideBefore(damaged), record]);
                Write(left);
                _records = left;
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogError("Cannot set aside a library rename: {Error}", exception.GetType().Name);
                return false;
            }
        }
    }

    // The records of a damaged file still readable as one, or none: what cannot be read is replaced, as Load does.
    private static List<LibraryRenameRecord> SetAsideBefore(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<JournalFile>(stream, _options) is { Version: CurrentVersion } file && file.Records.All(record => record is not null)
                ? file.Records.ToList()
                : [];
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    // Null while the file cannot be read for now; a damaged one is set aside and counts as empty.
    private List<LibraryRenameRecord>? Load()
    {
        if (_records is not null)
        {
            return _records;
        }

        try
        {
            using var stream = File.OpenRead(_path);
            var file = JsonSerializer.Deserialize<JournalFile>(stream, _options);
            if (file?.Version == CurrentVersion && file.Records.All(record => record is not null))
            {
                _records = file.Records.ToList();
                return _records;
            }

            _logger.LogError("The file of the library renames has an unsupported format. It is set aside");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            _logger.LogError("Cannot read the file of the library renames: {Error}. It is set aside", exception.GetType().Name);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            _records = [];
            return _records;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Cannot read the file of the library renames for now: {Error}", exception.GetType().Name);
            return null;
        }

        try
        {
            File.Move(_path, _path + ".damaged", overwrite: true);
            _records = [];
            return _records;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Cannot set aside the damaged file of the library renames: {Error}", exception.GetType().Name);
            return null;
        }
    }

    // Owner-only, flushed to disk, then moved over the file; no file at all when nothing is pending.
    private void Write(List<LibraryRenameRecord> records)
    {
        if (records.Count == 0)
        {
            File.Delete(_path);
            return;
        }

        Save(_path, records);
    }

    // Owner-only, flushed to disk, then moved over the file in one step.
    private static void Save(string path, List<LibraryRenameRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, new JournalFile(CurrentVersion, records), _options);
                stream.Flush(true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed record JournalFile(int Version, IReadOnlyList<LibraryRenameRecord> Records);
}
