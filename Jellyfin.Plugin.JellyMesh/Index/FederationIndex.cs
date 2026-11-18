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

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// <see cref="IFederationIndex"/> in memory, with the tables of the plugin database as its durable copy. A change is
/// written first, in one transaction, and only then made visible, so memory and the tables do not drift apart.
/// </summary>
public sealed class FederationIndex : IFederationIndex, IDisposable
{
    private readonly IPluginDatabase _database;
    private readonly ILogger<FederationIndex> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile IndexSnapshot _snapshot;

    // The fingerprints of the stored entries; null while the store could not be read, so memory is not its copy.
    private Dictionary<Guid, Dictionary<string, UInt128>>? _rows;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationIndex"/> class, reading the index from the database.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    /// <param name="logger">The logger.</param>
    public FederationIndex(IPluginDatabase database, ILogger<FederationIndex> logger)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
        _logger = logger;

        // Built once at startup, before any request; the read runs on a pool thread of its own, so the wait cannot deadlock.
        var stored = LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        _snapshot = stored?.Snapshot ?? IndexSnapshot.Empty(IndexHealth.Corrupt);
        _rows = stored?.Rows;
    }

    /// <inheritdoc />
    public IndexSnapshot Snapshot => _snapshot;

    /// <inheritdoc />
    public Task<bool> ReplaceServerAsync(Guid serverId, IReadOnlyList<IndexedItem> items, DateTimeOffset syncedUtc, SyncMark? mark, CancellationToken cancellationToken)
    {
        var catalog = new ServerCatalog
        {
            ServerId = serverId,
            State = ServerSyncState.Complete,
            LastAttemptUtc = syncedUtc,
            LastSuccessUtc = syncedUtc,
            Mark = mark,
            Items = items
        };

        // Real data arrived, so the damage is behind us: any server still missing is stale or unknown by itself.
        return CommitAsync(current => current with { Health = IndexHealth.Ok, Servers = WithServer(current, catalog) }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> MergeServerAsync(Guid serverId, IReadOnlyList<IndexedItem> changed, DateTimeOffset syncedUtc, SyncMark mark, CancellationToken cancellationToken)
    {
        return CommitAsync(
            current =>
            {
                if (current.Find(serverId) is not { State: ServerSyncState.Complete } catalog)
                {
                    return current;
                }

                return current with
                {
                    Servers = WithServer(current, catalog with { LastAttemptUtc = syncedUtc, Mark = mark, Items = changed.Count == 0 ? catalog.Items : Merged(catalog.Items, changed) })
                };
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> MarkServerFailedAsync(Guid serverId, RemoteFailure failure, DateTimeOffset attemptUtc, CancellationToken cancellationToken)
    {
        return CommitAsync(
            current =>
            {
                var catalog = current.Find(serverId) ?? new ServerCatalog { ServerId = serverId, State = ServerSyncState.Stale };
                return current with
                {
                    Servers = WithServer(current, catalog with { State = ServerSyncState.Stale, LastFailure = failure, LastAttemptUtc = attemptUtc })
                };
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DropServersAsync(IReadOnlySet<Guid> serverIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serverIds);

        return CommitAsync(
            current => current.Servers.Any(server => serverIds.Contains(server.ServerId))
                ? current with { Servers = current.Servers.Where(server => !serverIds.Contains(server.ServerId)).ToList() }
                : current,
            cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => _writeGate.Dispose();

    private static List<ServerCatalog> WithServer(IndexSnapshot snapshot, ServerCatalog catalog)
    {
        return [.. snapshot.Servers.Where(server => server.ServerId != catalog.ServerId), catalog];
    }

    private static List<IndexedItem> Merged(IReadOnlyList<IndexedItem> items, IReadOnlyList<IndexedItem> changed)
    {
        var updates = changed.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
        var known = items.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. items.Select(item => updates.GetValueOrDefault(item.ItemId, item)),
            .. updates.Values.Where(item => !known.Contains(item.ItemId))
        ];
    }

    // What changed between two snapshots, server by server; a catalog that is the same object did not change.
    private static List<IndexTables.ServerWrite> Plan(IndexSnapshot current, IndexSnapshot next, Dictionary<Guid, Dictionary<string, UInt128>> rows)
    {
        var writes = new List<IndexTables.ServerWrite>();
        var before = current.Servers.ToDictionary(server => server.ServerId);
        foreach (var catalog in next.Servers)
        {
            var previous = before.GetValueOrDefault(catalog.ServerId);
            if (!ReferenceEquals(previous, catalog) && IndexTables.Plan(catalog.ServerId, previous, catalog, rows.GetValueOrDefault(catalog.ServerId)) is { } write)
            {
                writes.Add(write);
            }
        }

        var kept = next.Servers.Select(server => server.ServerId).ToHashSet();
        foreach (var gone in before.Keys.Where(serverId => !kept.Contains(serverId)))
        {
            if (IndexTables.Plan(gone, before[gone], null, rows.GetValueOrDefault(gone)) is { } write)
            {
                writes.Add(write);
            }
        }

        return writes;
    }

    // Brings the fingerprints in step with a committed write.
    private static void Apply(Dictionary<Guid, Dictionary<string, UInt128>> rows, IReadOnlyList<IndexTables.ServerWrite> writes)
    {
        foreach (var write in writes)
        {
            if (write.Catalog is null)
            {
                rows.Remove(write.ServerId);
                continue;
            }

            if (write.Rewrite || !rows.TryGetValue(write.ServerId, out var stored))
            {
                rows[write.ServerId] = stored = new Dictionary<string, UInt128>(StringComparer.Ordinal);
            }

            foreach (var itemId in write.Deletes)
            {
                stored.Remove(itemId);
            }

            foreach (var row in write.Upserts)
            {
                stored[row.Item.ItemId] = row.Fingerprint;
            }
        }
    }

    // Null when the store is suspended or cannot be read, to be tried again before the next change. Rows that do not read
    // give an empty index whose servers are written whole at their next read. Either way it is empty for lack of data.
    private async Task<IndexTables.StoredIndex?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _database.ReadAsync(IndexTables.Load, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is StoreSuspendedException or SqliteException)
        {
            _logger.LogError("Cannot read the federation index from the database: {Error}. It is empty until the database can be read", exception.GetType().Name);
            return null;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            _logger.LogError("The federation index in the database is damaged: {Error}. It is empty until a sync fills it again", exception.GetType().Name);
            return new IndexTables.StoredIndex(IndexSnapshot.Empty(IndexHealth.Corrupt), []);
        }
    }

    private async Task<bool> CommitAsync(Func<IndexSnapshot, IndexSnapshot> change, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Memory is not the copy of the tables until they are read: a store that came back is read before any change.
            if (_rows is null)
            {
                if (await LoadAsync(cancellationToken).ConfigureAwait(false) is not { } stored)
                {
                    return false;
                }

                _snapshot = stored.Snapshot;
                _rows = stored.Rows;
            }

            var current = _snapshot;
            var next = change(current);
            if (ReferenceEquals(next, current))
            {
                return true;
            }

            var writes = Plan(current, next, _rows);
            if (writes.Count > 0)
            {
                await _database.WriteAsync((connection, transaction) => IndexTables.Write(connection, transaction, writes), cancellationToken).ConfigureAwait(false);
                Apply(_rows, writes);
            }

            _snapshot = next;
            return true;
        }
        catch (Exception exception) when (exception is StoreSuspendedException or SqliteException)
        {
            _logger.LogError("Cannot save the federation index: {Error}", exception.GetType().Name);
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
