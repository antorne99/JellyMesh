using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The tables of the settings, servers, peers and folder migration in the plugin database, one row per record (§3.1).
/// Credentials stay in the servers table as the ciphertext of <see cref="Security.ICredentialVault"/>.
/// </summary>
internal static class ConfigurationTables
{
    /// <summary>Settings by name, each value as invariant text.</summary>
    public const string Settings = "settings";

    /// <summary>Remote servers, in the order the page shows them.</summary>
    public const string Servers = "servers";

    /// <summary>The id and name lists of each server, told apart by <c>list</c>.</summary>
    public const string ServerValues = "server_values";

    /// <summary>The libraries the plugin created for each server.</summary>
    public const string ServerAutomaticLibraries = "server_automatic_libraries";

    /// <summary>The imported remote libraries of each server, as last read (schema step 6).</summary>
    public const string ServerRemoteLibraries = "server_remote_libraries";

    /// <summary>The libraries asked of the server for each server and not recorded yet (schema step 6).</summary>
    public const string ServerPendingLibraries = "server_pending_libraries";

    /// <summary>Removed servers whose libraries are still to delete (decision 140).</summary>
    public const string RemovedServers = "removed_servers";

    /// <summary>The id and name lists of each removed server, told apart by <c>list</c>.</summary>
    public const string RemovedServerValues = "removed_server_values";

    /// <summary>The libraries of each removed server.</summary>
    public const string RemovedServerLibraries = "removed_server_libraries";

    /// <summary>The single row of the migration from the flat layout.</summary>
    public const string LayoutMigration = "layout_migration";

    /// <summary>The servers the migration placed.</summary>
    public const string LayoutMigrationPlacedServers = "layout_migration_placed_servers";

    /// <summary>The JellyMesh servers connected to this one; they hold no secrets (§3.3).</summary>
    public const string Peers = "peers";

    /// <summary>The libraries shared with each peer.</summary>
    public const string PeerSharedLibraries = "peer_shared_libraries";

    private const string ListKey = "list TEXT NOT NULL, position INTEGER NOT NULL, value TEXT NOT NULL";
    private const string LibraryColumns = "position INTEGER NOT NULL, kind TEXT NOT NULL, name TEXT NOT NULL, item_id TEXT NOT NULL";

    /// <summary>
    /// Gets every table with its columns, in the order they are read, and the order of its rows.
    /// </summary>
    public static IReadOnlyList<Table> All { get; } =
    [
        new(Settings, ["name", "value"], "name"),
        new(Servers, ["id", "position", "jellyfin_server_id", "name", "url", "encrypted_credentials", "mode", "protocol_version", "automatic_libraries", "follow_skipped", "rename_unfinished", "library_mode"], "position"),
        new(ServerValues, ["server_id", "list", "position", "value"], "server_id, list, position"),
        new(ServerAutomaticLibraries, ["server_id", "position", "kind", "name", "item_id", "remote_library_id"], "server_id, position"),
        new(ServerRemoteLibraries, ["server_id", "position", "library_id", "name", "collection_type"], "server_id, position"),
        new(ServerPendingLibraries, ["server_id", "position", "kind", "name", "remote_library_id", "paths"], "server_id, position"),
        new(RemovedServers, ["server_id", "position", "follow_skipped"], "position"),
        new(RemovedServerValues, ["server_id", "list", "position", "value"], "server_id, list, position"),
        new(RemovedServerLibraries, ["server_id", "position", "kind", "name", "item_id", "remote_library_id"], "server_id, position"),
        new(LayoutMigration, ["id", "mode", "step", "error", "error_library"], "id"),
        new(LayoutMigrationPlacedServers, ["server_id", "position"], "position"),
        new(Peers, ["user_id", "position", "max_streams"], "position"),
        new(PeerSharedLibraries, ["user_id", "position", "library_id"], "user_id, position")
    ];

    /// <summary>
    /// Creates the tables, as schema step 4.
    /// </summary>
    /// <param name="connection">The writer connection.</param>
    /// <param name="transaction">The step's transaction.</param>
    public static void Create(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(
            connection,
            transaction,
            $"""
            CREATE TABLE {Settings} (name TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE {Servers} (
                id TEXT PRIMARY KEY, position INTEGER NOT NULL, jellyfin_server_id TEXT NOT NULL, name TEXT NOT NULL,
                url TEXT NOT NULL, encrypted_credentials TEXT NOT NULL, mode TEXT NOT NULL, protocol_version INTEGER NOT NULL,
                automatic_libraries INTEGER NOT NULL, follow_skipped INTEGER NOT NULL, rename_unfinished INTEGER NOT NULL) WITHOUT ROWID;
            CREATE TABLE {ServerValues} (server_id TEXT NOT NULL, {ListKey}, PRIMARY KEY (server_id, list, position)) WITHOUT ROWID;
            CREATE TABLE {ServerAutomaticLibraries} (server_id TEXT NOT NULL, {LibraryColumns}, PRIMARY KEY (server_id, position)) WITHOUT ROWID;
            CREATE TABLE {RemovedServers} (server_id TEXT PRIMARY KEY, position INTEGER NOT NULL, follow_skipped INTEGER NOT NULL) WITHOUT ROWID;
            CREATE TABLE {RemovedServerValues} (server_id TEXT NOT NULL, {ListKey}, PRIMARY KEY (server_id, list, position)) WITHOUT ROWID;
            CREATE TABLE {RemovedServerLibraries} (server_id TEXT NOT NULL, {LibraryColumns}, PRIMARY KEY (server_id, position)) WITHOUT ROWID;
            CREATE TABLE {LayoutMigration} (
                id INTEGER PRIMARY KEY CHECK (id = 1), mode TEXT NOT NULL, step TEXT NOT NULL, error TEXT NOT NULL, error_library TEXT NOT NULL);
            CREATE TABLE {LayoutMigrationPlacedServers} (server_id TEXT PRIMARY KEY, position INTEGER NOT NULL) WITHOUT ROWID;
            CREATE TABLE {Peers} (user_id TEXT PRIMARY KEY, position INTEGER NOT NULL, max_streams INTEGER NOT NULL) WITHOUT ROWID;
            CREATE TABLE {PeerSharedLibraries} (user_id TEXT NOT NULL, position INTEGER NOT NULL, library_id TEXT NOT NULL, PRIMARY KEY (user_id, position)) WITHOUT ROWID;
            """);
    }

    /// <summary>
    /// Adds the library modes, as schema step 6: the mode of each server, on per type where its switch was on and none
    /// where it was off (§10 row 445); the remote library of a plugin library; the imported remote libraries as last read;
    /// the libraries asked of the server and not recorded yet.
    /// </summary>
    /// <param name="connection">The writer connection.</param>
    /// <param name="transaction">The step's transaction.</param>
    public static void AddLibraryModes(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(
            connection,
            transaction,
            $"""
            ALTER TABLE {Servers} ADD COLUMN library_mode TEXT NOT NULL DEFAULT 'None';
            UPDATE {Servers} SET library_mode = CASE WHEN automatic_libraries <> 0 THEN 'ByType' ELSE 'None' END;
            ALTER TABLE {ServerAutomaticLibraries} ADD COLUMN remote_library_id TEXT NOT NULL DEFAULT '';
            ALTER TABLE {RemovedServerLibraries} ADD COLUMN remote_library_id TEXT NOT NULL DEFAULT '';
            CREATE TABLE {ServerRemoteLibraries} (
                server_id TEXT NOT NULL, position INTEGER NOT NULL, library_id TEXT NOT NULL, name TEXT NOT NULL,
                collection_type TEXT NOT NULL, PRIMARY KEY (server_id, position)) WITHOUT ROWID;
            CREATE TABLE {ServerPendingLibraries} (
                server_id TEXT NOT NULL, position INTEGER NOT NULL, kind TEXT NOT NULL, name TEXT NOT NULL,
                remote_library_id TEXT NOT NULL, paths TEXT NOT NULL, PRIMARY KEY (server_id, position)) WITHOUT ROWID;
            """);
    }

    /// <summary>
    /// Reads every table.
    /// </summary>
    /// <param name="connection">A read connection.</param>
    /// <param name="transaction">Its transaction.</param>
    /// <returns>The rows.</returns>
    public static ConfigurationRows Read(SqliteConnection connection, SqliteTransaction transaction)
    {
        var tables = new Dictionary<string, IReadOnlyList<object[]>>(StringComparer.Ordinal);
        foreach (var table in All)
        {
            using var command = Command(connection, transaction, $"SELECT {string.Join(", ", table.Columns)} FROM {table.Name} ORDER BY {table.OrderBy};");
            using var reader = command.ExecuteReader();
            var rows = new List<object[]>();
            while (reader.Read())
            {
                var row = new object[table.Columns.Count];
                for (var column = 0; column < row.Length; column++)
                {
                    row[column] = reader.GetValue(column);
                }

                rows.Add(row);
            }

            tables[table.Name] = rows;
        }

        return new ConfigurationRows(tables);
    }

    /// <summary>
    /// Writes the tables whose rows differ from those saved before, each replaced whole.
    /// </summary>
    /// <param name="connection">The writer connection.</param>
    /// <param name="transaction">The write transaction.</param>
    /// <param name="saved">The rows in the database.</param>
    /// <param name="current">The rows to save.</param>
    public static void Write(SqliteConnection connection, SqliteTransaction transaction, ConfigurationRows saved, ConfigurationRows current)
    {
        foreach (var table in All.Where(table => !saved.SameRows(current, table.Name)))
        {
            Execute(connection, transaction, $"DELETE FROM {table.Name};");
            using var insert = Command(
                connection,
                transaction,
                $"INSERT INTO {table.Name} ({string.Join(", ", table.Columns)}) VALUES ({string.Join(", ", table.Columns.Select((_, index) => "$p" + index))});");
            var parameters = table.Columns.Select((_, index) => insert.Parameters.Add(new SqliteParameter("$p" + index, null))).ToList();
            foreach (var row in current.Rows(table.Name))
            {
                for (var column = 0; column < row.Length; column++)
                {
                    parameters[column].Value = row[column];
                }

                insert.ExecuteNonQuery();
            }
        }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string text)
    {
        using var command = Command(connection, transaction, text);
        command.ExecuteNonQuery();
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Table and column names are constants of this class; values go in as parameters.")]
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string text)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        return command;
    }

    /// <summary>
    /// A table, its columns and the order of its rows.
    /// </summary>
    /// <param name="Name">The table.</param>
    /// <param name="Columns">Its columns, in the order of the values of a row.</param>
    /// <param name="OrderBy">The order of its rows.</param>
    internal sealed record Table(string Name, IReadOnlyList<string> Columns, string OrderBy);
}
