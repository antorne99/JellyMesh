using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The library renames whose user lists are not all rewritten yet, in the table <c>library_renames</c> of the plugin
/// database (§10 row 290). It holds library names and ids only. Each change is a short transaction of its own; a rename
/// set aside stays in the table, marked, and is never replayed.
/// </summary>
public sealed class LibraryRenameJournal
{
    /// <summary>
    /// The file of 0.5, which the data migration reads once.
    /// </summary>
    public const string FileName = "library-renames.json";

    private const string SelectPending = """
        SELECT id, old_name, new_name, old_id, new_id, type, temporary_name
        FROM library_renames WHERE set_aside_utc IS NULL ORDER BY id;
        """;

    private const string Insert = """
        INSERT INTO library_renames (old_name, new_name, old_id, new_id, type, temporary_name)
        VALUES ($oldName, $newName, $oldId, $newId, $type, $temporary)
        RETURNING id;
        """;

    private const string Delete = "DELETE FROM library_renames WHERE id = $id;";

    private const string MarkSetAside = "UPDATE library_renames SET set_aside_utc = $now WHERE id = $id;";

    private readonly IPluginDatabase _database;
    private readonly ILogger<LibraryRenameJournal> _logger;
    private readonly ErrorLogLimits _errorLogs;
    private readonly Lock _lock = new();
    private List<(long Id, LibraryRenameRecord Record)>? _records;

    // The database generation the renames were read in; another one means their ids may name other rows.
    private long _readGeneration;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryRenameJournal"/> class.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="errorLogs">The limits of the errors in the log.</param>
    public LibraryRenameJournal(IPluginDatabase database, ILogger<LibraryRenameJournal> logger, ErrorLogLimits? errorLogs = null)
    {
        _errorLogs = errorLogs ?? new ErrorLogLimits();
        _database = database;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether a rename is not done, or the database cannot be read for now.
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
    /// Gets the renames not done, empty while the database cannot be read.
    /// </summary>
    /// <returns>The records, oldest first.</returns>
    public IReadOnlyList<LibraryRenameRecord> Pending()
    {
        lock (_lock)
        {
            return Load()?.Select(entry => entry.Record).ToList() ?? [];
        }
    }

    /// <summary>
    /// Saves a rename before its folder moves.
    /// </summary>
    /// <param name="record">The rename.</param>
    /// <exception cref="IOException">The database cannot be read or written, so the rename must not start.</exception>
    public void Add(LibraryRenameRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_lock)
        {
            var records = Load() ?? throw new IOException("The library renames cannot be read.");
            try
            {
                var id = _database.WriteAsync((connection, transaction) => InsertRecord(connection, transaction, record), CancellationToken.None).GetAwaiter().GetResult();
                records.Add((id, record));
            }
            catch (Exception exception) when (exception is StoreSuspendedException or SqliteException)
            {
                throw new IOException("The library rename cannot be saved.", exception);
            }
        }
    }

    /// <summary>
    /// Drops a rename once every user is rewritten.
    /// </summary>
    /// <param name="record">The rename.</param>
    /// <returns>False when the database cannot be written, so the rename is replayed later.</returns>
    public bool Remove(LibraryRenameRecord record) => Change(record, setAside: false);

    /// <summary>
    /// Marks a rename the resume cannot trust as set aside, next to those set aside before, so it is never replayed
    /// (§10 row 329).
    /// </summary>
    /// <param name="record">The rename.</param>
    /// <returns>What became of the rename; it stays pending only while the database cannot be read or written.</returns>
    public SetAsideOutcome SetAside(LibraryRenameRecord record) =>
        Change(record, setAside: true) ? SetAsideOutcome.SetAside : SetAsideOutcome.Pending;

    internal static long InsertRecord(SqliteConnection connection, SqliteTransaction transaction, LibraryRenameRecord record)
    {
        using var command = StoreSql.Command(
            connection,
            transaction,
            Insert,
            ("$oldName", record.OldName),
            ("$newName", record.NewName),
            ("$oldId", StoreSql.Text(record.OldId)),
            ("$newId", StoreSql.Text(record.NewId)),
            ("$type", record.Type?.ToString()),
            ("$temporary", record.TemporaryName));
        return (long)command.ExecuteScalar()!;
    }

    private static List<(long Id, LibraryRenameRecord Record)> ReadPending(SqliteConnection connection, SqliteTransaction transaction)
    {
        var records = new List<(long, LibraryRenameRecord)>();
        using var command = StoreSql.Command(connection, transaction, SelectPending);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var type = StoreSql.TextOrNull(reader, 5) is { } text && Enum.TryParse<CollectionType>(text, out var parsed) ? parsed : (CollectionType?)null;
            records.Add((
                reader.GetInt64(0),
                new LibraryRenameRecord(
                    reader.GetString(1),
                    reader.GetString(2),
                    Guid.ParseExact(reader.GetString(3), "N"),
                    Guid.ParseExact(reader.GetString(4), "N"),
                    type,
                    StoreSql.TextOrNull(reader, 6))));
        }

        return records;
    }

    // Drops or marks the rows of the record and takes them out of the pending ones; false when it cannot.
    private bool Change(LibraryRenameRecord record, bool setAside)
    {
        lock (_lock)
        {
            if (Load() is not { } records)
            {
                return false;
            }

            var ids = records.Where(entry => entry.Record == record).Select(entry => entry.Id).ToList();
            try
            {
                var now = DateTime.UtcNow.Ticks;
                var generation = _readGeneration;
                _database.WriteAsync(
                    (connection, transaction) =>
                    {
                        ThrowIfReopened(generation);
                        foreach (var id in ids)
                        {
                            if (setAside)
                            {
                                StoreSql.Execute(connection, transaction, MarkSetAside, ("$id", id), ("$now", now));
                            }
                            else
                            {
                                StoreSql.Execute(connection, transaction, Delete, ("$id", id));
                            }
                        }
                    },
                    CancellationToken.None).GetAwaiter().GetResult();
                records.RemoveAll(entry => ids.Contains(entry.Id));
                return true;
            }
            catch (Exception exception) when (exception is StoreSuspendedException or SqliteException)
            {
                _logger.LogError("Cannot save the library renames: {Error}{Detail:l}", exception.GetType().Name, _errorLogs.Local.Of(exception));
                return false;
            }
        }
    }

    // Refuses a change planned on ids of an earlier opening of the database, rolling it back.
    private void ThrowIfReopened(long generation)
    {
        if (_database.Generation != generation)
        {
            throw new StoreSuspendedException("The database was opened again since the library renames were read.");
        }
    }

    // Called with the lock held: the pending renames, null while the database cannot be read for now.
    private List<(long Id, LibraryRenameRecord Record)>? Load()
    {
        if (_records is not null && _readGeneration == _database.Generation)
        {
            return _records;
        }

        try
        {
            (_records, _readGeneration) = _database.ReadAsync(
                (connection, transaction) => (ReadPending(connection, transaction), _database.Generation),
                CancellationToken.None).GetAwaiter().GetResult();
            return _records;
        }
        catch (Exception exception) when (exception is StoreSuspendedException or SqliteException)
        {
            _logger.LogWarning("Cannot read the library renames for now: {Error}{Detail:l}", exception.GetType().Name, _errorLogs.Local.Of(exception));
            return null;
        }
    }
}
