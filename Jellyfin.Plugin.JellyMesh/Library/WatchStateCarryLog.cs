using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="IWatchStateCarryLog"/> over the table <c>watch_state_carries</c> of the plugin database. It holds user and
/// item IDs and a date. A carry recorded or forgotten is written at once in a short transaction; one that cannot be is
/// written by the next save. While the database cannot be read the carries are suspended until it reads (decision 176).
/// </summary>
public sealed class WatchStateCarryLog : IWatchStateCarryLog, IDisposable
{
    /// <summary>
    /// The file of 0.5, which the data migration reads once.
    /// </summary>
    public const string FileName = "watch-state-carries.json";

    private const string SelectAll = "SELECT user_id, item_id, last_played_ticks FROM watch_state_carries;";

    private const string Upsert = """
        INSERT INTO watch_state_carries (user_id, item_id, last_played_ticks) VALUES ($user, $item, $played)
        ON CONFLICT (user_id, item_id) DO UPDATE SET last_played_ticks = excluded.last_played_ticks;
        """;

    private const string Delete = "DELETE FROM watch_state_carries WHERE user_id = $user AND item_id = $item;";

    private readonly IPluginDatabase _database;
    private readonly ILogger<WatchStateCarryLog> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId), DateTime?> _carries = new();
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
    public WatchStateCarryLog(IPluginDatabase database, ILogger<WatchStateCarryLog> logger)
    {
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

                foreach (var (key, lastPlayed) in rows.Where(row => !_unsaved.ContainsKey(row.Key)))
                {
                    _carries[key] = lastPlayed;
                }

                Interlocked.Exchange(ref _loadedGeneration, generation);
                _loaded = true;
            }

            return true;
        }
        catch (Exception exception) when (exception is StoreSuspendedException or SqliteException)
        {
            _logger.LogWarning("Cannot read the watch state carries done for now: {Error}. Carries are suspended until the database reads{Detail:l}", exception.GetType().Name, ErrorText.Of(exception));
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public bool TryFind(Guid userId, Guid fromItemId, out DateTime? lastPlayedDate)
    {
        lastPlayedDate = null;
        return EnsureLoaded() && _carries.TryGetValue((userId, fromItemId), out lastPlayedDate);
    }

    /// <inheritdoc />
    public void Record(Guid userId, Guid fromItemId, DateTime? lastPlayedDate)
    {
        if (!EnsureLoaded())
        {
            return;
        }

        var key = (userId, fromItemId);
        _carries[key] = lastPlayedDate;
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

    private static List<KeyValuePair<(Guid UserId, Guid ItemId), DateTime?>> ReadAll(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<KeyValuePair<(Guid, Guid), DateTime?>>();
        using var command = StoreSql.Command(connection, transaction, SelectAll);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var lastPlayed = StoreSql.LongOrNull(reader, 2) is { } ticks ? new DateTime(ticks, DateTimeKind.Utc) : (DateTime?)null;
            rows.Add(new((Guid.ParseExact(reader.GetString(0), "N"), Guid.ParseExact(reader.GetString(1), "N")), lastPlayed));
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
                        if (_carries.TryGetValue(key, out var lastPlayed))
                        {
                            StoreSql.Execute(connection, transaction, Upsert, ("$user", user), ("$item", item), ("$played", lastPlayed?.Ticks));
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
                _logger.LogError("Cannot save the watch state carries done: {Error}{Detail:l}", exception.GetType().Name, ErrorText.Of(exception));
            }

            return false;
        }
    }
}
