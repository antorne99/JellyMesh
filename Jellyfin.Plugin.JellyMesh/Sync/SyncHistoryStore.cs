using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// <see cref="ISyncHistory"/> over the tables <c>sync_runs</c> and <c>sync_run_servers</c> of the plugin database. A run is
/// inserted and the oldest dropped in one short transaction. While the database cannot be read the history shows only the
/// runs since, which are saved with the next run that can be.
/// </summary>
public sealed class SyncHistoryStore : ISyncHistory, IDisposable
{
    /// <summary>
    /// The file of 0.5, which the data migration reads once.
    /// </summary>
    public const string FileName = "sync-history.json";

    /// <summary>
    /// How many of the last runs are kept.
    /// </summary>
    public const int Capacity = 50;

    /// <summary>
    /// How many of the last reconciliations are kept, also when older than the last <see cref="Capacity"/> runs (decision 194).
    /// </summary>
    public const int ReconciliationCapacity = 7;

    private const string SelectRuns = """
        SELECT id, started_utc, offset_minutes, duration_ms, kind, outcome, created, updated, removed, failed, index_save_failed,
            materialization_failed
        FROM sync_runs ORDER BY id DESC;
        """;

    private const string SelectServers = """
        SELECT run_id, server_id, server_name, read, created, updated, removed, failed, failure, warning
        FROM sync_run_servers ORDER BY run_id, position;
        """;

    private const string InsertRun = """
        INSERT INTO sync_runs (started_utc, offset_minutes, duration_ms, kind, outcome, created, updated, removed, failed,
            index_save_failed, materialization_failed)
        VALUES ($started, $offset, $duration, $kind, $outcome, $created, $updated, $removed, $failed, $indexFailed, $materializationFailed)
        RETURNING id;
        """;

    private const string InsertServer = """
        INSERT INTO sync_run_servers (run_id, position, server_id, server_name, read, created, updated, removed, failed, failure, warning)
        VALUES ($run, $position, $server, $name, $read, $created, $updated, $removed, $failed, $failure, $warning);
        """;

    private const string SelectKinds = "SELECT id, kind FROM sync_runs ORDER BY id DESC;";

    private const string DeleteRun = """
        DELETE FROM sync_run_servers WHERE run_id = $id;
        DELETE FROM sync_runs WHERE id = $id;
        """;

    private readonly IPluginDatabase _database;
    private readonly ILogger<SyncHistoryStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private List<SyncRun> _unsaved = [];
    private IReadOnlyList<SyncRun>? _saved;

    // The database generation the saved runs were read in; another one means they may be stale.
    private long _savedGeneration;
    private volatile IReadOnlyList<SyncRun> _runs = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncHistoryStore"/> class.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    /// <param name="logger">The logger.</param>
    public SyncHistoryStore(IPluginDatabase database, ILogger<SyncHistoryStore> logger)
    {
        _database = database;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<SyncRun> Runs
    {
        get
        {
            Load();
            return _runs;
        }
    }

    /// <inheritdoc />
    public bool IsDamaged => !Load();

    /// <inheritdoc />
    public async Task AddAsync(SyncRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<SyncRun> unsaved;
            lock (_lock)
            {
                _unsaved = Trim([run, .. _unsaved], entry => entry.Kind);
                unsaved = _unsaved;
                Publish();
            }

            if (!Load())
            {
                throw new StoreSuspendedException("The sync history cannot be read, so the run is not saved yet.");
            }

            var (saved, generation) = await _database.WriteAsync(
                (connection, transaction) => (Save(connection, transaction, unsaved), _database.Generation),
                cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _unsaved = [];
                (_saved, _savedGeneration) = (saved, generation);
                Publish();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    // Keeps the last runs and the last reconciliations, newest first. By position, since two runs can be equal.
    private static List<T> Trim<T>(IEnumerable<T> runs, Func<T, SyncRunKind> kindOf)
    {
        var kept = new List<T>();
        var index = 0;
        var reconciliations = 0;
        foreach (var run in runs)
        {
            var isLastReconciliation = kindOf(run) == SyncRunKind.Reconciliation && reconciliations++ < ReconciliationCapacity;
            if (index++ < Capacity || isLastReconciliation)
            {
                kept.Add(run);
            }
        }

        return kept;
    }

    // Inserts the unsaved runs oldest first, drops what the limits leave out, and reads the history back.
    internal static List<SyncRun> Save(SqliteConnection connection, SqliteTransaction transaction, List<SyncRun> unsaved)
    {
        foreach (var run in Enumerable.Reverse(unsaved))
        {
            long id;
            using (var command = StoreSql.Command(
                connection,
                transaction,
                InsertRun,
                ("$started", run.StartedUtc.UtcTicks),
                ("$offset", (int)run.StartedUtc.Offset.TotalMinutes),
                ("$duration", run.DurationMilliseconds),
                ("$kind", run.Kind.ToString()),
                ("$outcome", run.Outcome.ToString()),
                ("$created", run.Created),
                ("$updated", run.Updated),
                ("$removed", run.Removed),
                ("$failed", run.Failed),
                ("$indexFailed", run.IndexSaveFailed),
                ("$materializationFailed", run.MaterializationFailed)))
            {
                id = (long)command.ExecuteScalar()!;
            }

            for (var position = 0; position < run.Servers.Count; position++)
            {
                var server = run.Servers[position];
                StoreSql.Execute(
                    connection,
                    transaction,
                    InsertServer,
                    ("$run", id),
                    ("$position", position),
                    ("$server", StoreSql.Text(server.ServerId)),
                    ("$name", server.ServerName),
                    ("$read", server.Read),
                    ("$created", server.Created),
                    ("$updated", server.Updated),
                    ("$removed", server.Removed),
                    ("$failed", server.Failed),
                    ("$failure", server.Failure?.ToString()),
                    ("$warning", server.Warning is { } warning ? JsonSerializer.Serialize(warning) : null));
            }
        }

        var all = new List<(long Id, SyncRunKind Kind)>();
        using (var command = StoreSql.Command(connection, transaction, SelectKinds))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                all.Add((reader.GetInt64(0), Enum.Parse<SyncRunKind>(reader.GetString(1))));
            }
        }

        var kept = Trim(all, entry => entry.Kind).Select(entry => entry.Id).ToHashSet();
        foreach (var (id, _) in all.Where(entry => !kept.Contains(entry.Id)))
        {
            StoreSql.Execute(connection, transaction, DeleteRun, ("$id", id));
        }

        return ReadAll(connection, transaction);
    }

    private static List<SyncRun> ReadAll(SqliteConnection connection, SqliteTransaction transaction)
    {
        var servers = new Dictionary<long, List<SyncRunServer>>();
        using (var command = StoreSql.Command(connection, transaction, SelectServers))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var runId = reader.GetInt64(0);
                if (!servers.TryGetValue(runId, out var list))
                {
                    servers[runId] = list = [];
                }

                list.Add(new SyncRunServer(
                    Guid.ParseExact(reader.GetString(1), "N"),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    StoreSql.TextOrNull(reader, 8) is { } failure && Enum.TryParse<RemoteFailure>(failure, out var parsed) ? parsed : null)
                {
                    Warning = ReadWarning(StoreSql.TextOrNull(reader, 9))
                });
            }
        }

        var runs = new List<SyncRun>();
        using (var command = StoreSql.Command(connection, transaction, SelectRuns))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var offset = TimeSpan.FromMinutes(reader.GetInt32(2));
                runs.Add(new SyncRun(
                    new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero).ToOffset(offset),
                    reader.GetInt64(3),
                    Enum.Parse<SyncRunKind>(reader.GetString(4)),
                    Enum.Parse<SyncRunOutcome>(reader.GetString(5)),
                    servers.GetValueOrDefault(id) ?? [])
                {
                    Created = reader.GetInt32(6),
                    Updated = reader.GetInt32(7),
                    Removed = reader.GetInt32(8),
                    Failed = reader.GetInt32(9),
                    IndexSaveFailed = reader.GetInt64(10) != 0,
                    MaterializationFailed = reader.GetInt64(11) != 0
                });
            }
        }

        return runs;
    }

    // A warning that does not parse is left out, as the rest of the run still reads.
    private static SyncRunWarning? ReadWarning(string? text)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SyncRunWarning>(text) is { VanishedLibraries: not null } warning ? warning : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Called with the lock held: the runs not saved yet before the saved ones, within the limits.
    private void Publish() => _runs = Trim(_unsaved.Concat(_saved ?? []), run => run.Kind);

    // Reads the saved runs once the database can be read, and again after it is opened again, and tells whether they are known.
    private bool Load()
    {
        IReadOnlyList<SyncRun>? known;
        lock (_lock)
        {
            if (_saved is not null && _savedGeneration == _database.Generation)
            {
                return true;
            }

            known = _saved;
        }

        try
        {
            var (saved, generation) = _database.ReadAsync(
                (connection, transaction) => (ReadAll(connection, transaction), _database.Generation),
                CancellationToken.None).GetAwaiter().GetResult();
            lock (_lock)
            {
                // A run saved meanwhile is newer than this read.
                if (ReferenceEquals(_saved, known))
                {
                    (_saved, _savedGeneration) = (saved, generation);
                }

                Publish();
            }

            return true;
        }
        catch (Exception exception) when (exception is StoreSuspendedException or SqliteException)
        {
            _logger.LogDebug("Cannot read the sync history for now: {Error}", exception.GetType().Name);
            return false;
        }
    }
}
