using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="IWatchStateCarryLog"/> over the table <c>watch_state_carries</c> of the plugin database. It holds user and
/// item IDs, a date and the operation of the carry. A carry recorded or forgotten is written at once in a short transaction; one that cannot be is
/// written by the next save. While the database cannot be read the carries are suspended until it reads (decision 176).
/// </summary>
public sealed class WatchStateCarryLog : IWatchStateCarryLog, IDisposable
{
    /// <summary>
    /// The file of 0.5, which the data migration reads once.
    /// </summary>
    public const string FileName = "watch-state-carries.json";

    private const string SelectAll = "SELECT user_id, item_id, last_played_ticks, operation FROM watch_state_carries;";

    private const string Upsert = """
        INSERT INTO watch_state_carries (user_id, item_id, last_played_ticks, operation) VALUES ($user, $item, $played, $operation)
        ON CONFLICT (user_id, item_id) DO UPDATE SET last_played_ticks = excluded.last_played_ticks, operation = excluded.operation;
        """;

    private const string Delete = "DELETE FROM watch_state_carries WHERE user_id = $user AND item_id = $item;";

    private readonly IPluginDatabase _database;
    private readonly ILogger<WatchStateCarryLog> _logger;
    private readonly ErrorLogLimits _errorLogs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId), Carry> _carries = new();
    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId), byte> _unsaved = new();
    private volatile bool _loaded;

    // The database generation the carries were read in; another one means they may be stale.
    private long _loadedGeneration;
    private int _reported;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchStateCarryLog"/> class.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="errorLogs">The limits of the errors in the log.</param>
    public WatchStateCarryLog(IPluginDatabase database, ILogger<WatchStateCarryLog> logger, ErrorLogLimits? errorLogs = null)
    {
        _errorLogs = errorLogs ?? new ErrorLogLimits();
        _database = database;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool EnsureLoaded()
    {
        if (IsCurrent())
        {
            return true;
        }

        _gate.Wait();
        try
        {
            // Read again after the database is opened again; the changes not written yet stay.
            if (!IsCurrent())
            {
                var (rows, generation) = _database.ReadAsync(
                    (connection, transaction) => (ReadAll(connection, transaction), _database.Generation),
                    CancellationToken.None).GetAwaiter().GetResult();
                foreach (var key in _carries.Keys.Where(key => !_unsaved.ContainsKey(key)))
                {
                    _carries.TryRemove(key, out _);
                }

                foreach (var (key, carry) in rows.Where(row => !_unsaved.ContainsKey(row.Key)))
                {
                    _carries[key] = carry;
                }

                Interlocked.Exchange(ref _loadedGeneration, generation);
                _loaded = true;
            }

            return true;
        }
        catch (Exception exception) when (exception is StoreSuspendedException or SqliteException)
        {
            _logger.LogWarning("Cannot read the watch state carries done for now: {Error}. Carries are suspended until the database reads{Detail:l}", exception.GetType().Name, _errorLogs.Local.Of(exception));
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public bool TryFind(Guid userId, Guid fromItemId, out DateTime? lastPlayedDate) => TryFind(userId, fromItemId, out lastPlayedDate, out _);

    /// <inheritdoc />
    public bool TryFind(Guid userId, Guid fromItemId, out DateTime? lastPlayedDate, out WatchStateCarryOperation? operation)
    {
        (lastPlayedDate, operation) = (null, null);
        if (!EnsureLoaded() || !_carries.TryGetValue((userId, fromItemId), out var carry))
        {
            return false;
        }

        (lastPlayedDate, operation) = (carry.LastPlayedDate, carry.Operation);
        return true;
    }

    /// <inheritdoc />
    public void Record(Guid userId, Guid fromItemId, DateTime? lastPlayedDate, WatchStateCarryOperation operation)
    {
        if (!EnsureLoaded())
        {
            return;
        }

        var key = (userId, fromItemId);

        // Only a rename changes the operation of a record, so that a later round of it still finds its own (row 575).
        var kept = operation != WatchStateCarryOperation.Rename && _carries.TryGetValue(key, out var before) ? before.Operation : operation;
        _carries[key] = new Carry(lastPlayedDate, kept);
        _unsaved[key] = 0;
        WriteKeysAsync([key], CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public int Forget(Func<Guid, Guid, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (!EnsureLoaded())
        {
            return 0;
        }

        var forgotten = new List<(Guid UserId, Guid ItemId)>();
        foreach (var key in _carries.Keys.Where(key => match(key.UserId, key.ItemId)).ToList())
        {
            if (_carries.TryRemove(key, out _))
            {
                _unsaved[key] = 0;
                forgotten.Add(key);
            }
        }

        if (forgotten.Count > 0)
        {
            WriteKeysAsync(forgotten, CancellationToken.None).GetAwaiter().GetResult();
        }

        return forgotten.Count;
    }

    /// <inheritdoc />
    public async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        if (!EnsureLoaded())
        {
            return false;
        }

        return _unsaved.IsEmpty || await WriteKeysAsync(_unsaved.Keys.ToList(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private bool IsCurrent() => _loaded && Interlocked.Read(ref _loadedGeneration) == _database.Generation;

    // A name this build does not know reads as no operation, as a row made before row 575.
    private static WatchStateCarryOperation? OperationOf(string? name) =>
        Enum.TryParse<WatchStateCarryOperation>(name, false, out var operation) && string.Equals(Enum.GetName(operation), name, StringComparison.Ordinal)
            ? operation
            : null;

    private static List<KeyValuePair<(Guid UserId, Guid ItemId), Carry>> ReadAll(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<KeyValuePair<(Guid, Guid), Carry>>();
        using var command = StoreSql.Command(connection, transaction, SelectAll);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var lastPlayed = StoreSql.LongOrNull(reader, 2) is { } ticks ? new DateTime(ticks, DateTimeKind.Utc) : (DateTime?)null;
            var carry = new Carry(lastPlayed, OperationOf(StoreSql.TextOrNull(reader, 3)));
            rows.Add(new((Guid.ParseExact(reader.GetString(0), "N"), Guid.ParseExact(reader.GetString(1), "N")), carry));
        }

        return rows;
    }

    // Writes the current date of each key, or drops its row when it is forgotten; the keys stay unsaved when it cannot.
    private async Task<bool> WriteKeysAsync(List<(Guid UserId, Guid ItemId)> keys, CancellationToken cancellationToken)
    {
        foreach (var key in keys)
        {
            _unsaved.TryRemove(key, out _);
        }

        try
        {
            await _database.WriteAsync(
                (connection, transaction) =>
                {
                    foreach (var key in keys)
                    {
                        var (user, item) = (StoreSql.Text(key.UserId), StoreSql.Text(key.ItemId));
                        if (_carries.TryGetValue(key, out var carry))
                        {
                            StoreSql.Execute(connection, transaction, Upsert, ("$user", user), ("$item", item), ("$played", carry.LastPlayedDate?.Ticks), ("$operation", carry.Operation?.ToString()));
                        }
                        else
                        {
                            StoreSql.Execute(connection, transaction, Delete, ("$user", user), ("$item", item));
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _reported, 0);
            return true;
        }
        catch (Exception exception) when (exception is StoreSuspendedException or SqliteException or OperationCanceledException)
        {
            foreach (var key in keys)
            {
                _unsaved.TryAdd(key, 0);
            }

            if (exception is not OperationCanceledException && Interlocked.Exchange(ref _reported, 1) == 0)
            {
                _logger.LogError("Cannot save the watch state carries done: {Error}{Detail:l}", exception.GetType().Name, _errorLogs.Local.Of(exception));
            }

            return false;
        }
    }

    // The user's last watch of the old item when it was carried, and the operation of the carry, null if none is known.
    private readonly record struct Carry(DateTime? LastPlayedDate, WatchStateCarryOperation? Operation);
}
