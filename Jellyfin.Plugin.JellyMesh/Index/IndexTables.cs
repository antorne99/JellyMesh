using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// The tables of the federation index: one row per server, per library of its mark and per entry. An entry keeps its
/// nested detail (versions, tracks, people, chapters) as the JSON of that one entry.
/// </summary>
internal static class IndexTables
{
    // The nullable and required checks make a row with missing parts fail to load, instead of loading with holes.
    private static readonly JsonSerializerOptions _options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    /// <summary>
    /// Creates the tables, inside the transaction of a schema step.
    /// </summary>
    /// <param name="connection">The writer connection.</param>
    /// <param name="transaction">The transaction of the step.</param>
    public static void Create(SqliteConnection connection, SqliteTransaction transaction) => StoreCommands.Execute(
        connection,
        transaction,
        """
        CREATE TABLE index_servers (
            server_id TEXT NOT NULL PRIMARY KEY,
            state TEXT NOT NULL,
            last_failure TEXT,
            last_attempt_utc TEXT,
            last_success_utc TEXT,
            mark_read_started_utc TEXT
        ) WITHOUT ROWID;
        CREATE TABLE index_mark_libraries (
            server_id TEXT NOT NULL,
            position INTEGER NOT NULL,
            library_id TEXT NOT NULL,
            PRIMARY KEY (server_id, position)
        ) WITHOUT ROWID;
        CREATE TABLE index_items (
            server_id TEXT NOT NULL,
            item_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            library_id TEXT NOT NULL,
            last_seen_utc TEXT NOT NULL,
            data TEXT NOT NULL,
            PRIMARY KEY (server_id, item_id)
        );
        """);

    /// <summary>
    /// Reads the whole index and the fingerprint of each stored entry.
    /// </summary>
    /// <param name="connection">A read connection.</param>
    /// <param name="transaction">The read transaction.</param>
    /// <returns>The index and its rows.</returns>
    /// <exception cref="JsonException">An entry cannot be read.</exception>
    public static StoredIndex Load(SqliteConnection connection, SqliteTransaction transaction)
    {
        var marks = new Dictionary<Guid, List<string>>();
        using (var command = StoreCommands.Command(connection, transaction, "SELECT server_id, library_id FROM index_mark_libraries ORDER BY server_id, position;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var serverId = reader.GetId(0);
                if (!marks.TryGetValue(serverId, out var libraries))
                {
                    marks[serverId] = libraries = [];
                }

                libraries.Add(reader.GetString(1));
            }
        }

        var items = new Dictionary<Guid, List<IndexedItem>>();
        var rows = new Dictionary<Guid, Dictionary<string, UInt128>>();
        using (var command = StoreCommands.Command(connection, transaction, "SELECT server_id, item_id, last_seen_utc, data FROM index_items ORDER BY rowid;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var serverId = reader.GetId(0);
                var data = reader.GetString(3);
                var item = JsonSerializer.Deserialize<IndexedItem>(data, _options) ?? throw new JsonException("An index entry is empty.");
                if (!items.TryGetValue(serverId, out var list))
                {
                    items[serverId] = list = [];
                    rows[serverId] = new Dictionary<string, UInt128>(StringComparer.Ordinal);
                }

                list.Add(item with { LastSeenUtc = reader.GetTimeOrNull(2)!.Value });
                rows[serverId][reader.GetString(1)] = Fingerprint(data);
            }
        }

        var servers = new List<ServerCatalog>();
        using (var command = StoreCommands.Command(connection, transaction, "SELECT server_id, state, last_failure, last_attempt_utc, last_success_utc, mark_read_started_utc FROM index_servers ORDER BY server_id;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var serverId = reader.GetId(0);
                var markStarted = reader.GetTimeOrNull(5);
                servers.Add(new ServerCatalog
                {
                    ServerId = serverId,
                    State = Enum.Parse<ServerSyncState>(reader.GetString(1)),
                    LastFailure = reader.GetTextOrNull(2) is { } failure ? Enum.Parse<RemoteFailure>(failure) : null,
                    LastAttemptUtc = reader.GetTimeOrNull(3),
                    LastSuccessUtc = reader.GetTimeOrNull(4),
                    Mark = markStarted is { } started ? new SyncMark(started, marks.GetValueOrDefault(serverId) ?? []) : null,
                    Items = items.GetValueOrDefault(serverId) ?? []
                });
                rows.TryAdd(serverId, new Dictionary<string, UInt128>(StringComparer.Ordinal));
            }
        }

        // Entries of a server with no row of its own are never written together; drop them rather than show them.
        foreach (var orphan in rows.Keys.Where(serverId => servers.All(server => server.ServerId != serverId)).ToList())
        {
            rows.Remove(orphan);
        }

        return new StoredIndex(new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = IndexHealth.Ok, Servers = servers }, rows);
    }

    /// <summary>
    /// Works out what one server needs written to go from the stored rows to <paramref name="next"/>. Times alone never
    /// make a write: they go along with a real change (D27).
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="previous">The catalog in memory before the change; null if there was none.</param>
    /// <param name="next">The catalog after the change; null when the server goes.</param>
    /// <param name="stored">The fingerprints of the stored entries; null when the tables may hold rows memory does not know.</param>
    /// <returns>The write, or null when nothing needs writing.</returns>
    public static ServerWrite? Plan(Guid serverId, ServerCatalog? previous, ServerCatalog? next, IReadOnlyDictionary<string, UInt128>? stored)
    {
        if (next is null)
        {
            return previous is null && stored is null ? null : new ServerWrite(serverId, null, true, [], []);
        }

        var rewrite = stored is null;
        var upserts = new List<ItemRow>();
        var deletes = new List<string>();
        if (rewrite)
        {
            upserts.AddRange(next.Items.Select(Row));
        }
        else if (!ReferenceEquals(previous?.Items, next.Items))
        {
            var before = new Dictionary<string, IndexedItem>(StringComparer.Ordinal);
            foreach (var item in previous?.Items ?? [])
            {
                before[item.ItemId] = item;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in next.Items)
            {
                seen.Add(item.ItemId);
                if (before.TryGetValue(item.ItemId, out var old) && ReferenceEquals(old, item) && stored!.ContainsKey(item.ItemId))
                {
                    continue;
                }

                var row = Row(item);
                if (!stored!.TryGetValue(item.ItemId, out var fingerprint) || fingerprint != row.Fingerprint)
                {
                    upserts.Add(row);
                }
            }

            deletes.AddRange(stored!.Keys.Where(itemId => !seen.Contains(itemId)));
        }

        var changed = rewrite || previous is null || upserts.Count > 0 || deletes.Count > 0
            || previous.State != next.State
            || previous.LastFailure != next.LastFailure
            || (previous.Mark is null) != (next.Mark is null)
            || !(previous.Mark?.LibraryIds ?? []).SequenceEqual(next.Mark?.LibraryIds ?? [], StringComparer.Ordinal);
        return changed ? new ServerWrite(serverId, next, rewrite, upserts, deletes) : null;
    }

    /// <summary>
    /// Writes the planned changes of some servers, inside the caller's transaction.
    /// </summary>
    /// <param name="connection">The writer connection.</param>
    /// <param name="transaction">The write transaction.</param>
    /// <param name="writes">The writes.</param>
    public static void Write(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<ServerWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);

        using var deleteItems = StoreCommands.Prepared(connection, transaction, "DELETE FROM index_items WHERE server_id = $server;", "$server");
        using var deleteMark = StoreCommands.Prepared(connection, transaction, "DELETE FROM index_mark_libraries WHERE server_id = $server;", "$server");
        using var deleteServer = StoreCommands.Prepared(connection, transaction, "DELETE FROM index_servers WHERE server_id = $server;", "$server");
        using var upsertServer = StoreCommands.Prepared(
            connection,
            transaction,
            """
            INSERT INTO index_servers (server_id, state, last_failure, last_attempt_utc, last_success_utc, mark_read_started_utc)
            VALUES ($server, $state, $failure, $attempt, $success, $mark)
            ON CONFLICT (server_id) DO UPDATE SET state = excluded.state, last_failure = excluded.last_failure,
                last_attempt_utc = excluded.last_attempt_utc, last_success_utc = excluded.last_success_utc,
                mark_read_started_utc = excluded.mark_read_started_utc;
            """,
            "$server",
            "$state",
            "$failure",
            "$attempt",
            "$success",
            "$mark");
        using var insertMark = StoreCommands.Prepared(connection, transaction, "INSERT INTO index_mark_libraries (server_id, position, library_id) VALUES ($server, $position, $library);", "$server", "$position", "$library");
        using var deleteItem = StoreCommands.Prepared(connection, transaction, "DELETE FROM index_items WHERE server_id = $server AND item_id = $item;", "$server", "$item");
        using var upsertItem = StoreCommands.Prepared(
            connection,
            transaction,
            """
            INSERT INTO index_items (server_id, item_id, kind, library_id, last_seen_utc, data)
            VALUES ($server, $item, $kind, $library, $seen, $data)
            ON CONFLICT (server_id, item_id) DO UPDATE SET kind = excluded.kind, library_id = excluded.library_id,
                last_seen_utc = excluded.last_seen_utc, data = excluded.data;
            """,
            "$server",
            "$item",
            "$kind",
            "$library",
            "$seen",
            "$data");

        foreach (var write in writes)
        {
            var server = StoreCommands.Text(write.ServerId);
            if (write.Catalog is not { } catalog)
            {
                deleteItems.Run(server);
                deleteMark.Run(server);
                deleteServer.Run(server);
                continue;
            }

            upsertServer.Run(
                server,
                catalog.State.ToString(),
                catalog.LastFailure?.ToString(),
                StoreCommands.Text(catalog.LastAttemptUtc),
                StoreCommands.Text(catalog.LastSuccessUtc),
                StoreCommands.Text(catalog.Mark?.ReadStartedUtc));
            deleteMark.Run(server);
            var libraries = catalog.Mark?.LibraryIds ?? [];
            for (var position = 0; position < libraries.Count; position++)
            {
                insertMark.Run(server, position, libraries[position]);
            }

            if (write.Rewrite)
            {
                deleteItems.Run(server);
            }

            foreach (var itemId in write.Deletes)
            {
                deleteItem.Run(server, itemId);
            }

            foreach (var row in write.Upserts)
            {
                upsertItem.Run(server, row.Item.ItemId, row.Item.Kind.ToString(), row.Item.LibraryId, StoreCommands.Text(row.Item.LastSeenUtc), row.Data);
            }
        }
    }

    // The time an entry was seen is a column of its own, so that a new sighting alone does not change the data.
    private static ItemRow Row(IndexedItem item)
    {
        var data = JsonSerializer.Serialize(item with { LastSeenUtc = default }, _options);
        return new ItemRow(item, data, Fingerprint(data));
    }

    private static UInt128 Fingerprint(string data) => BinaryPrimitives.ReadUInt128LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(data)));

    /// <summary>
    /// One stored entry.
    /// </summary>
    /// <param name="Item">The entry.</param>
    /// <param name="Data">Its JSON, without the time it was seen.</param>
    /// <param name="Fingerprint">The hash of <paramref name="Data"/>.</param>
    internal readonly record struct ItemRow(IndexedItem Item, string Data, UInt128 Fingerprint);

    /// <summary>
    /// The index as the tables hold it, with the fingerprint of each entry by server and item.
    /// </summary>
    /// <param name="Snapshot">The index.</param>
    /// <param name="Rows">The fingerprints.</param>
    internal sealed record StoredIndex(IndexSnapshot Snapshot, Dictionary<Guid, Dictionary<string, UInt128>> Rows);

    /// <summary>
    /// What one server needs written.
    /// </summary>
    /// <param name="ServerId">The server.</param>
    /// <param name="Catalog">Its catalog; null when it goes.</param>
    /// <param name="Rewrite">Whether its stored entries are dropped first, because memory does not know them.</param>
    /// <param name="Upserts">The entries to add or replace.</param>
    /// <param name="Deletes">The item IDs to delete.</param>
    internal sealed record ServerWrite(Guid ServerId, ServerCatalog? Catalog, bool Rewrite, IReadOnlyList<ItemRow> Upserts, IReadOnlyList<string> Deletes);
}
