using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// <see cref="ILastServedSourceStore"/> over the table <c>last_served_sources</c> of the plugin database. It holds the
/// user, the stub and the server GUID, never an address or a token. A change is known at once and written right after,
/// off the request, in a short transaction of its own; a change that cannot be written is tried again with the next one.
/// </summary>
public sealed class LastServedSourceStore : ILastServedSourceStore, IHostedService, IDisposable
{
    /// <summary>
    /// The file of 0.5, which the data migration reads once.
    /// </summary>
    public const string FileName = "last-served.json";

    private const string SelectAll = "SELECT user_id, stub_id, server_id FROM last_served_sources;";

    private const string Upsert = """
        INSERT INTO last_served_sources (user_id, stub_id, server_id) VALUES ($user, $stub, $server)
        ON CONFLICT (user_id, stub_id) DO UPDATE SET server_id = excluded.server_id;
        """;

    private const string Delete = "DELETE FROM last_served_sources WHERE user_id = $user AND stub_id = $stub;";

    private readonly IPluginDatabase _database;
    private readonly ILogger<LastServedSourceStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<(Guid UserId, string StubId), Guid> _servers = new();
    private readonly ConcurrentDictionary<(Guid UserId, string StubId), byte> _unsaved = new();
    private readonly Lock _loadLock = new();
    private volatile bool _loaded;

    // The database generation the records were read in; another one means they may be stale.
    private long _loadedGeneration;
    private volatile bool _disposed;
    private int _reported;

    /// <summary>
    /// Initializes a new instance of the <see cref="LastServedSourceStore"/> class.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    /// <param name="logger">The logger.</param>
    public LastServedSourceStore(IPluginDatabase database, ILogger<LastServedSourceStore> logger)
    {
        _database = database;
        _logger = logger;
    }

    /// <inheritdoc />
    public Guid? Find(Guid userId, string stubId)
    {
        Load();
        return _servers.TryGetValue((userId, stubId), out var serverId) ? serverId : null;
    }

    /// <inheritdoc />
    public Task RecordAsync(Guid userId, string stubId, Guid serverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stubId);

        Load();
        var key = (userId, stubId);
        if (!_servers.TryGetValue(key, out var known) || known != serverId)
        {
            _servers[key] = serverId;
            Changed(key);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public int Prune(Func<string, bool> stubExists, IReadOnlySet<Guid> removedServers)
    {
        ArgumentNullException.ThrowIfNull(stubExists);
        ArgumentNullException.ThrowIfNull(removedServers);

        Load();
        var dropped = 0;
        foreach (var (key, serverId) in _servers)
        {
            if ((removedServers.Contains(serverId) || !stubExists(key.StubId)) && _servers.TryRemove(new KeyValuePair<(Guid, string), Guid>(key, serverId)))
            {
                dropped++;
                Changed(key);
            }
        }

        return dropped;
    }

    /// <summary>
    /// Writes now the changes not written yet, once the writes started before it end.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_unsaved.IsEmpty || !Load())
            {
                return;
            }

            // Taken first: a change during the write is written by the next one.
            var keys = _unsaved.Keys.ToList();
            foreach (var key in keys)
            {
                _unsaved.TryRemove(key, out _);
            }

            try
            {
                await _database.WriteAsync((connection, transaction) => Save(connection, transaction, keys), cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _reported, 0);
            }
            catch (Exception exception) when (exception is StoreSuspendedException or SqliteException or OperationCanceledException)
            {
                foreach (var key in keys)
                {
                    _unsaved.TryAdd(key, 0);
                }

                if (exception is not OperationCanceledException && Interlocked.Exchange(ref _reported, 1) == 0)
                {
                    _logger.LogWarning("Cannot save the last server that served a title: {Error}{Detail:l}", exception.GetType().Name, ErrorText.Of(exception));
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => FlushAsync(CancellationToken.None);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        _disposed = true;
        _gate.Dispose();
    }

    private static List<KeyValuePair<(Guid UserId, string StubId), Guid>> ReadAll(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<KeyValuePair<(Guid, string), Guid>>();
        using var command = StoreSql.Command(connection, transaction, SelectAll);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new((Guid.ParseExact(reader.GetString(0), "N"), reader.GetString(1)), Guid.ParseExact(reader.GetString(2), "N")));
        }

        return rows;
    }

    // Writes the current server of each key, or drops its row when it has none.
    private void Save(SqliteConnection connection, SqliteTransaction transaction, List<(Guid UserId, string StubId)> keys)
    {
        foreach (var key in keys)
        {
            var user = StoreSql.Text(key.UserId);
            if (_servers.TryGetValue(key, out var serverId))
            {
                StoreSql.Execute(connection, transaction, Upsert, ("$user", user), ("$stub", key.StubId), ("$server", StoreSql.Text(serverId)));
            }
            else
            {
                StoreSql.Execute(connection, transaction, Delete, ("$user", user), ("$stub", key.StubId));
            }
        }
    }

    // Marks a change and starts its write off the caller.
    private void Changed((Guid UserId, string StubId) key)
    {
        _unsaved[key] = 0;
        if (!_disposed)
        {
            _ = Task.Run(WriteLaterAsync);
        }
    }

    private async Task WriteLaterAsync()
    {
        try
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Closed meanwhile: the close wrote it.
        }
    }

    // Reads the saved records once the database can be read, and again after it is opened again; the changes not written
    // yet stay.
    private bool Load()
    {
        if (IsCurrent())
        {
            return true;
        }

        lock (_loadLock)
        {
            if (IsCurrent())
            {
                return true;
            }

            try
            {
                var (rows, generation) = _database.ReadAsync(
                    (connection, transaction) => (ReadAll(connection, transaction), _database.Generation),
                    CancellationToken.None).GetAwaiter().GetResult();
                foreach (var key in _servers.Keys.Where(key => !_unsaved.ContainsKey(key)))
                {
                    _servers.TryRemove(key, out _);
                }

                foreach (var (key, serverId) in rows.Where(row => !_unsaved.ContainsKey(row.Key)))
                {
                    _servers.TryAdd(key, serverId);
                }

                Interlocked.Exchange(ref _loadedGeneration, generation);
                _loaded = true;
                return true;
            }
            catch (Exception exception) when (exception is StoreSuspendedException or SqliteException)
            {
                if (Interlocked.Exchange(ref _reported, 1) == 0)
                {
                    _logger.LogWarning("Cannot read the last servers that served each title for now: {Error}{Detail:l}", exception.GetType().Name, ErrorText.Of(exception));
                }

                return false;
            }
        }
    }

    private bool IsCurrent() => _loaded && Interlocked.Read(ref _loadedGeneration) == _database.Generation;
}
