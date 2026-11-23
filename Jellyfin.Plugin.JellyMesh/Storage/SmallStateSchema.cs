using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// The tables of the small state: manual merges, last served sources, sync history, library renames, watch state carries
/// and the watch state kept for removed servers. Ids are GUID text in "N" form, times UTC ticks, enums their names.
/// </summary>
public static class SmallStateSchema
{
    /// <summary>The schema version these tables come with.</summary>
    public const int Version = 3;

    private const string Tables = """
        CREATE TABLE manual_merges (
            id TEXT NOT NULL PRIMARY KEY,
            created_utc INTEGER NOT NULL);
        CREATE TABLE manual_merge_copies (
            merge_id TEXT NOT NULL,
            position INTEGER NOT NULL,
            server_id TEXT NOT NULL,
            item_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            is_local INTEGER NOT NULL,
            tmdb TEXT,
            imdb TEXT,
            tvdb TEXT,
            title TEXT,
            year INTEGER,
            PRIMARY KEY (merge_id, position)) WITHOUT ROWID;
        CREATE TABLE last_served_sources (
            user_id TEXT NOT NULL,
            stub_id TEXT NOT NULL,
            server_id TEXT NOT NULL,
            PRIMARY KEY (user_id, stub_id)) WITHOUT ROWID;
        CREATE TABLE sync_runs (
            id INTEGER PRIMARY KEY,
            started_utc INTEGER NOT NULL,
            offset_minutes INTEGER NOT NULL,
            duration_ms INTEGER NOT NULL,
            kind TEXT NOT NULL,
            outcome TEXT NOT NULL,
            created INTEGER NOT NULL,
            updated INTEGER NOT NULL,
            removed INTEGER NOT NULL,
            failed INTEGER NOT NULL,
            index_save_failed INTEGER NOT NULL,
            materialization_failed INTEGER NOT NULL);
        CREATE TABLE sync_run_servers (
            run_id INTEGER NOT NULL,
            position INTEGER NOT NULL,
            server_id TEXT NOT NULL,
            server_name TEXT NOT NULL,
            read INTEGER NOT NULL,
            created INTEGER NOT NULL,
            updated INTEGER NOT NULL,
            removed INTEGER NOT NULL,
            failed INTEGER NOT NULL,
            failure TEXT,
            PRIMARY KEY (run_id, position)) WITHOUT ROWID;
        CREATE TABLE library_renames (
            id INTEGER PRIMARY KEY,
            old_name TEXT NOT NULL,
            new_name TEXT NOT NULL,
            old_id TEXT NOT NULL,
            new_id TEXT NOT NULL,
            type TEXT,
            temporary_name TEXT,
            set_aside_utc INTEGER);
        CREATE TABLE watch_state_carries (
            user_id TEXT NOT NULL,
            item_id TEXT NOT NULL,
            last_played_ticks INTEGER,
            PRIMARY KEY (user_id, item_id)) WITHOUT ROWID;
        CREATE TABLE kept_titles (
            id INTEGER PRIMARY KEY,
            old_item_id TEXT NOT NULL UNIQUE,
            kind TEXT NOT NULL,
            title TEXT,
            year INTEGER,
            tmdb TEXT,
            imdb TEXT,
            tvdb TEXT,
            original_title TEXT,
            run_time_ticks INTEGER,
            overview TEXT,
            genres TEXT NOT NULL,
            studios TEXT NOT NULL,
            people TEXT NOT NULL,
            season_number INTEGER,
            episode_number INTEGER,
            episode_number_end INTEGER,
            saved_utc INTEGER NOT NULL);
        CREATE TABLE kept_user_states (
            title_id INTEGER NOT NULL,
            user_id TEXT NOT NULL,
            played INTEGER NOT NULL,
            play_count INTEGER NOT NULL,
            playback_position_ticks INTEGER NOT NULL,
            last_played_utc INTEGER,
            is_favorite INTEGER NOT NULL,
            rating REAL,
            PRIMARY KEY (title_id, user_id)) WITHOUT ROWID;
        CREATE INDEX kept_user_states_by_user ON kept_user_states (user_id);
        CREATE TABLE kept_title_copies (
            title_id INTEGER NOT NULL,
            position INTEGER NOT NULL,
            server_id TEXT NOT NULL,
            item_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            is_local INTEGER NOT NULL,
            tmdb TEXT,
            imdb TEXT,
            tvdb TEXT,
            title TEXT,
            year INTEGER,
            PRIMARY KEY (title_id, position)) WITHOUT ROWID;
        CREATE TABLE kept_merge_copies (
            removed_server_id TEXT NOT NULL,
            merge_id TEXT NOT NULL,
            position INTEGER NOT NULL,
            server_id TEXT NOT NULL,
            item_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            is_local INTEGER NOT NULL,
            tmdb TEXT,
            imdb TEXT,
            tvdb TEXT,
            title TEXT,
            year INTEGER,
            saved_utc INTEGER NOT NULL,
            PRIMARY KEY (removed_server_id, merge_id, position)) WITHOUT ROWID;
        """;

    /// <summary>
    /// Adds the warning of each server to the sync history (F38), inside the step's transaction.
    /// </summary>
    /// <param name="connection">The open database.</param>
    /// <param name="transaction">The step's transaction.</param>
    public static void AddWarnings(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "ALTER TABLE sync_run_servers ADD COLUMN warning TEXT;";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds the artists of a kept music video (F53), inside the step's transaction. Titles kept before have none.
    /// </summary>
    /// <param name="connection">The open database.</param>
    /// <param name="transaction">The step's transaction.</param>
    public static void AddKeptArtists(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "ALTER TABLE kept_titles ADD COLUMN artists TEXT NOT NULL DEFAULT '[]';";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds the data of a kept track, audiobook, book or home video (row 532), inside the step's transaction. Titles kept
    /// before have none.
    /// </summary>
    /// <param name="connection">The open database.</param>
    /// <param name="transaction">The step's transaction.</param>
    public static void AddKeptTypes(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE kept_titles ADD COLUMN album_artists TEXT NOT NULL DEFAULT '[]';
            ALTER TABLE kept_titles ADD COLUMN album TEXT;
            ALTER TABLE kept_titles ADD COLUMN series_name TEXT;
            ALTER TABLE kept_titles ADD COLUMN provider_ids TEXT NOT NULL DEFAULT '{}';
            ALTER TABLE kept_titles ADD COLUMN place TEXT;
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates the tables, inside the step's transaction.
    /// </summary>
    /// <param name="connection">The writer connection.</param>
    /// <param name="transaction">The step's transaction.</param>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The text is a constant.")]
    public static void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        System.ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Tables;
        command.ExecuteNonQuery();
    }
}
