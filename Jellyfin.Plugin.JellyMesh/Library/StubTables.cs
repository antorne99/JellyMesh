using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The tables of the stub registry: one row per stub, per series stub and per version group, with their lists in tables
/// of their own, and the labels, names, removal marks and renames of the servers.
/// </summary>
internal static class StubTables
{
    private static readonly IReadOnlyList<StubSource> _noSources = [];
    private static readonly IReadOnlyList<SeasonStub> _noSeasons = [];

    /// <summary>
    /// Creates the tables, inside the transaction of a schema step.
    /// </summary>
    /// <param name="connection">The writer connection.</param>
    /// <param name="transaction">The transaction of the step.</param>
    public static void Create(SqliteConnection connection, SqliteTransaction transaction) => StoreCommands.Execute(
        connection,
        transaction,
        """
        CREATE TABLE stub_entries (
            id TEXT NOT NULL PRIMARY KEY,
            ordinal INTEGER NOT NULL,
            server_id TEXT,
            kind TEXT NOT NULL,
            relative_path TEXT NOT NULL,
            identity_key TEXT NOT NULL,
            series_stub_id TEXT,
            season_number INTEGER,
            episode_number INTEGER,
            episode_number_end INTEGER,
            content_hash TEXT,
            image_tag TEXT,
            technical_tag TEXT,
            url_tag TEXT,
            strm_written_ticks INTEGER
        ) WITHOUT ROWID;
        CREATE TABLE stub_entry_sources (
            stub_id TEXT NOT NULL,
            position INTEGER NOT NULL,
            server_id TEXT NOT NULL,
            item_id TEXT NOT NULL,
            media_source_id TEXT NOT NULL,
            library_id TEXT,
            PRIMARY KEY (stub_id, position)
        ) WITHOUT ROWID;
        CREATE TABLE stub_series (
            id TEXT NOT NULL PRIMARY KEY,
            ordinal INTEGER NOT NULL,
            server_id TEXT,
            relative_path TEXT NOT NULL,
            work_key TEXT NOT NULL,
            content_hash TEXT,
            image_tag TEXT,
            needs_refresh INTEGER NOT NULL
        ) WITHOUT ROWID;
        CREATE TABLE stub_series_sources (
            series_id TEXT NOT NULL,
            position INTEGER NOT NULL,
            server_id TEXT NOT NULL,
            item_id TEXT NOT NULL,
            media_source_id TEXT NOT NULL,
            library_id TEXT,
            PRIMARY KEY (series_id, position)
        ) WITHOUT ROWID;
        CREATE TABLE stub_series_seasons (
            series_id TEXT NOT NULL,
            position INTEGER NOT NULL,
            relative_path TEXT NOT NULL,
            content_hash TEXT,
            image_tag TEXT,
            PRIMARY KEY (series_id, position)
        ) WITHOUT ROWID;
        CREATE TABLE stub_groups (
            position INTEGER NOT NULL PRIMARY KEY,
            id TEXT NOT NULL,
            identity_key TEXT NOT NULL,
            primary_stub_id TEXT
        );
        CREATE TABLE stub_group_stubs (
            group_position INTEGER NOT NULL,
            position INTEGER NOT NULL,
            stub_id TEXT NOT NULL,
            PRIMARY KEY (group_position, position)
        ) WITHOUT ROWID;
        CREATE TABLE stub_group_local_items (
            group_position INTEGER NOT NULL,
            position INTEGER NOT NULL,
            item_id TEXT NOT NULL,
            PRIMARY KEY (group_position, position)
        ) WITHOUT ROWID;
        CREATE TABLE stub_labels (
            server_id TEXT NOT NULL PRIMARY KEY,
            label TEXT NOT NULL
        ) WITHOUT ROWID;
        CREATE TABLE stub_server_names (
            server_id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL
        ) WITHOUT ROWID;
        CREATE TABLE stub_removed_servers (
            position INTEGER NOT NULL PRIMARY KEY,
            server_id TEXT NOT NULL
        );
        CREATE TABLE stub_renames (
            position INTEGER NOT NULL PRIMARY KEY,
            stub_id TEXT NOT NULL,
            old_path TEXT NOT NULL,
            removed_item_id TEXT,
            forgets INTEGER NOT NULL
        );
        """);

    /// <summary>
    /// Reads the whole registry.
    /// </summary>
    /// <param name="connection">A read connection.</param>
    /// <param name="transaction">The read transaction.</param>
    /// <returns>The state.</returns>
    /// <exception cref="FormatException">A row holds a value that does not read.</exception>
    /// <exception cref="ArgumentException">A row holds a name that does not read.</exception>
    public static StubState Load(SqliteConnection connection, SqliteTransaction transaction)
    {
        var entrySources = ReadSources(connection, transaction, "SELECT stub_id, server_id, item_id, media_source_id, library_id FROM stub_entry_sources ORDER BY stub_id, position;");
        var stubs = new List<StubEntry>();
        using (var command = StoreCommands.Command(
            connection,
            transaction,
            """
            SELECT id, server_id, kind, relative_path, identity_key, series_stub_id, season_number, episode_number,
                episode_number_end, content_hash, image_tag, technical_tag, url_tag, strm_written_ticks
            FROM stub_entries ORDER BY ordinal;
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                stubs.Add(new StubEntry
                {
                    Id = id,
                    ServerId = reader.GetIdOrNull(1),
                    Kind = Enum.Parse<StubKind>(reader.GetString(2)),
                    RelativePath = reader.GetString(3),
                    IdentityKey = reader.GetString(4),
                    SeriesStubId = reader.GetTextOrNull(5),
                    SeasonNumber = reader.GetInt32OrNull(6),
                    EpisodeNumber = reader.GetInt32OrNull(7),
                    EpisodeNumberEnd = reader.GetInt32OrNull(8),
                    ContentHash = reader.GetTextOrNull(9),
                    ImageTag = reader.GetTextOrNull(10),
                    TechnicalTag = reader.GetTextOrNull(11),
                    UrlTag = reader.GetTextOrNull(12),
                    StrmWrittenUtc = reader.IsDBNull(13) ? null : new DateTime(reader.GetInt64(13), DateTimeKind.Utc),
                    Sources = entrySources.GetValueOrDefault(id) ?? []
                });
            }
        }

        var seriesSources = ReadSources(connection, transaction, "SELECT series_id, server_id, item_id, media_source_id, library_id FROM stub_series_sources ORDER BY series_id, position;");
        var seasons = new Dictionary<string, List<SeasonStub>>(StringComparer.Ordinal);
        using (var command = StoreCommands.Command(connection, transaction, "SELECT series_id, relative_path, content_hash, image_tag FROM stub_series_seasons ORDER BY series_id, position;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                Add(seasons, reader.GetString(0), new SeasonStub { RelativePath = reader.GetString(1), ContentHash = reader.GetTextOrNull(2), ImageTag = reader.GetTextOrNull(3) });
            }
        }

        var series = new List<SeriesStub>();
        using (var command = StoreCommands.Command(connection, transaction, "SELECT id, server_id, relative_path, work_key, content_hash, image_tag, needs_refresh FROM stub_series ORDER BY ordinal;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                series.Add(new SeriesStub
                {
                    Id = id,
                    ServerId = reader.GetIdOrNull(1),
                    RelativePath = reader.GetString(2),
                    WorkKey = reader.GetString(3),
                    ContentHash = reader.GetTextOrNull(4),
                    ImageTag = reader.GetTextOrNull(5),
                    NeedsRefresh = reader.GetBoolean(6),
                    Sources = seriesSources.GetValueOrDefault(id) ?? [],
                    Seasons = seasons.GetValueOrDefault(id) ?? []
                });
            }
        }

        return new StubState
        {
            Stubs = stubs,
            Series = series,
            Groups = ReadGroups(connection, transaction),
            Labels = ReadNames(connection, transaction, "SELECT server_id, label FROM stub_labels;"),
            ServerNames = ReadNames(connection, transaction, "SELECT server_id, name FROM stub_server_names;"),
            RemovedServers = ReadRemoved(connection, transaction),
            Renames = ReadRenames(connection, transaction)
        };
    }

    /// <summary>
    /// Works out what goes from <paramref name="stored"/> to <paramref name="next"/>: the stubs and series that changed,
    /// came or went, and the smaller lists that changed, which are written again whole.
    /// </summary>
    /// <param name="stored">What the tables hold; null when that is not known, so everything is written again.</param>
    /// <param name="next">The state to store.</param>
    /// <returns>The write, or null when nothing changed.</returns>
    public static StubWrite? Plan(StubState? stored, StubState next)
    {
        ArgumentNullException.ThrowIfNull(next);

        var rewrite = stored is null;
        var before = stored ?? StubState.Empty;
        var write = new StubWrite(next, rewrite)
        {
            Groups = rewrite || !before.Groups.SequenceEqual(next.Groups, GroupComparer.Instance),
            Labels = rewrite || !SameNames(before.Labels, next.Labels),
            ServerNames = rewrite || !SameNames(before.ServerNames, next.ServerNames),
            RemovedServers = rewrite || !before.RemovedServers.SequenceEqual(next.RemovedServers),
            Renames = rewrite || !before.Renames.SequenceEqual(next.Renames)
        };

        PlanRows(before.Stubs, next.Stubs, stub => stub.Id, SameStub, write.StubRows, write.StubOrdinals, write.StubDeletes);
        PlanRows(before.Series, next.Series, series => series.Id, SameSeries, write.SeriesRows, write.SeriesOrdinals, write.SeriesDeletes);
        return write.IsEmpty ? null : write;
    }

    /// <summary>
    /// Writes a planned change, inside the caller's transaction.
    /// </summary>
    /// <param name="connection">The writer connection.</param>
    /// <param name="transaction">The write transaction.</param>
    /// <param name="write">The change.</param>
    public static void Write(SqliteConnection connection, SqliteTransaction transaction, StubWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);

        if (write.Rewrite)
        {
            StoreCommands.Execute(
                connection,
                transaction,
                """
                DELETE FROM stub_entries; DELETE FROM stub_entry_sources; DELETE FROM stub_series; DELETE FROM stub_series_sources;
                DELETE FROM stub_series_seasons;
                """);
        }

        WriteStubs(connection, transaction, write);
        WriteSeries(connection, transaction, write);
        var state = write.State;
        if (write.Groups)
        {
            WriteGroups(connection, transaction, state.Groups);
        }

        if (write.Labels)
        {
            WriteNames(connection, transaction, "DELETE FROM stub_labels;", "INSERT INTO stub_labels (server_id, label) VALUES ($server, $value);", state.Labels);
        }

        if (write.ServerNames)
        {
            WriteNames(connection, transaction, "DELETE FROM stub_server_names;", "INSERT INTO stub_server_names (server_id, name) VALUES ($server, $value);", state.ServerNames);
        }

        if (write.RemovedServers)
        {
            StoreCommands.Execute(connection, transaction, "DELETE FROM stub_removed_servers;");
            using var insert = StoreCommands.Prepared(connection, transaction, "INSERT INTO stub_removed_servers (position, server_id) VALUES ($position, $server);", "$position", "$server");
            for (var position = 0; position < state.RemovedServers.Count; position++)
            {
                insert.Run(position, StoreCommands.Text(state.RemovedServers[position]));
            }
        }

        if (write.Renames)
        {
            StoreCommands.Execute(connection, transaction, "DELETE FROM stub_renames;");
            using var insert = StoreCommands.Prepared(
                connection,
                transaction,
                "INSERT INTO stub_renames (position, stub_id, old_path, removed_item_id, forgets) VALUES ($position, $stub, $path, $removed, $forgets);",
                "$position",
                "$stub",
                "$path",
                "$removed",
                "$forgets");
            for (var position = 0; position < state.Renames.Count; position++)
            {
                var rename = state.Renames[position];
                insert.Run(position, rename.StubId, rename.OldPath, StoreCommands.Text(rename.RemovedItemId), rename.Forgets);
            }
        }
    }

    // A row whose content is the same but whose place in the list moved only gets its new place.
    private static void PlanRows<T>(
        IReadOnlyList<T> before,
        IReadOnlyList<T> next,
        Func<T, string> id,
        Func<T, T, bool> same,
        List<(T Row, int Ordinal)> rows,
        List<(string Id, int Ordinal)> ordinals,
        List<string> deletes)
        where T : class
    {
        var old = new Dictionary<string, (T Row, int Ordinal)>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < before.Count; ordinal++)
        {
            old.TryAdd(id(before[ordinal]), (before[ordinal], ordinal));
        }

        var kept = new HashSet<string>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < next.Count; ordinal++)
        {
            var row = next[ordinal];
            var key = id(row);
            kept.Add(key);
            if (!old.TryGetValue(key, out var stored) || !(ReferenceEquals(stored.Row, row) || same(stored.Row, row)))
            {
                rows.Add((row, ordinal));
            }
            else if (stored.Ordinal != ordinal)
            {
                ordinals.Add((key, ordinal));
            }
        }

        deletes.AddRange(old.Keys.Where(key => !kept.Contains(key)));

        // Rows go in key order, so that each page of a table is written about once instead of again for every row.
        rows.Sort((a, b) => string.CompareOrdinal(id(a.Row), id(b.Row)));
        ordinals.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        deletes.Sort(StringComparer.Ordinal);
    }

    private static bool SameStub(StubEntry a, StubEntry b)
        => a with { Sources = _noSources } == b with { Sources = _noSources } && a.Sources.SequenceEqual(b.Sources);

    private static bool SameSeries(SeriesStub a, SeriesStub b)
        => a with { Sources = _noSources, Seasons = _noSeasons } == b with { Sources = _noSources, Seasons = _noSeasons }
            && a.Sources.SequenceEqual(b.Sources)
            && a.Seasons.SequenceEqual(b.Seasons);

    private static bool SameNames(IReadOnlyDictionary<Guid, string> a, IReadOnlyDictionary<Guid, string> b)
        => a.Count == b.Count && a.All(entry => b.TryGetValue(entry.Key, out var value) && string.Equals(value, entry.Value, StringComparison.Ordinal));

    private static void WriteStubs(SqliteConnection connection, SqliteTransaction transaction, StubWrite write)
    {
        using var upsert = StoreCommands.Prepared(
            connection,
            transaction,
            """
            INSERT INTO stub_entries (id, ordinal, server_id, kind, relative_path, identity_key, series_stub_id, season_number,
                episode_number, episode_number_end, content_hash, image_tag, technical_tag, url_tag, strm_written_ticks)
            VALUES ($id, $ordinal, $server, $kind, $path, $identity, $series, $season, $episode, $end, $content, $image,
                $technical, $url, $strm)
            ON CONFLICT (id) DO UPDATE SET ordinal = excluded.ordinal, server_id = excluded.server_id, kind = excluded.kind,
                relative_path = excluded.relative_path, identity_key = excluded.identity_key,
                series_stub_id = excluded.series_stub_id, season_number = excluded.season_number,
                episode_number = excluded.episode_number, episode_number_end = excluded.episode_number_end,
                content_hash = excluded.content_hash, image_tag = excluded.image_tag, technical_tag = excluded.technical_tag,
                url_tag = excluded.url_tag, strm_written_ticks = excluded.strm_written_ticks;
            """,
            "$id",
            "$ordinal",
            "$server",
            "$kind",
            "$path",
            "$identity",
            "$series",
            "$season",
            "$episode",
            "$end",
            "$content",
            "$image",
            "$technical",
            "$url",
            "$strm");
        using var move = StoreCommands.Prepared(connection, transaction, "UPDATE stub_entries SET ordinal = $ordinal WHERE id = $id;", "$ordinal", "$id");
        using var delete = StoreCommands.Prepared(connection, transaction, "DELETE FROM stub_entries WHERE id = $id;", "$id");
        using var sources = new SourceWriter(connection, transaction, "stub_entry_sources", "stub_id");

        foreach (var stubId in write.StubDeletes)
        {
            delete.Run(stubId);
            sources.Clear(stubId);
        }

        foreach (var (stubId, ordinal) in write.StubOrdinals)
        {
            move.Run(ordinal, stubId);
        }

        foreach (var (stub, ordinal) in write.StubRows)
        {
            upsert.Run(
                stub.Id,
                ordinal,
                StoreCommands.Text(stub.ServerId),
                stub.Kind.ToString(),
                stub.RelativePath,
                stub.IdentityKey,
                stub.SeriesStubId,
                stub.SeasonNumber,
                stub.EpisodeNumber,
                stub.EpisodeNumberEnd,
                stub.ContentHash,
                stub.ImageTag,
                stub.TechnicalTag,
                stub.UrlTag,
                stub.StrmWrittenUtc?.Ticks);
            sources.Replace(stub.Id, stub.Sources);
        }
    }

    private static void WriteSeries(SqliteConnection connection, SqliteTransaction transaction, StubWrite write)
    {
        using var upsert = StoreCommands.Prepared(
            connection,
            transaction,
            """
            INSERT INTO stub_series (id, ordinal, server_id, relative_path, work_key, content_hash, image_tag, needs_refresh)
            VALUES ($id, $ordinal, $server, $path, $work, $content, $image, $refresh)
            ON CONFLICT (id) DO UPDATE SET ordinal = excluded.ordinal, server_id = excluded.server_id,
                relative_path = excluded.relative_path, work_key = excluded.work_key, content_hash = excluded.content_hash,
                image_tag = excluded.image_tag, needs_refresh = excluded.needs_refresh;
            """,
            "$id",
            "$ordinal",
            "$server",
            "$path",
            "$work",
            "$content",
            "$image",
            "$refresh");
        using var move = StoreCommands.Prepared(connection, transaction, "UPDATE stub_series SET ordinal = $ordinal WHERE id = $id;", "$ordinal", "$id");
        using var delete = StoreCommands.Prepared(connection, transaction, "DELETE FROM stub_series WHERE id = $id;", "$id");
        using var clearSeasons = StoreCommands.Prepared(connection, transaction, "DELETE FROM stub_series_seasons WHERE series_id = $id;", "$id");
        using var insertSeason = StoreCommands.Prepared(
            connection,
            transaction,
            "INSERT INTO stub_series_seasons (series_id, position, relative_path, content_hash, image_tag) VALUES ($id, $position, $path, $content, $image);",
            "$id",
            "$position",
            "$path",
            "$content",
            "$image");
        using var sources = new SourceWriter(connection, transaction, "stub_series_sources", "series_id");

        foreach (var seriesId in write.SeriesDeletes)
        {
            delete.Run(seriesId);
            sources.Clear(seriesId);
            clearSeasons.Run(seriesId);
        }

        foreach (var (seriesId, ordinal) in write.SeriesOrdinals)
        {
            move.Run(ordinal, seriesId);
        }

        foreach (var (series, ordinal) in write.SeriesRows)
        {
            upsert.Run(series.Id, ordinal, StoreCommands.Text(series.ServerId), series.RelativePath, series.WorkKey, series.ContentHash, series.ImageTag, series.NeedsRefresh);
            sources.Replace(series.Id, series.Sources);
            clearSeasons.Run(series.Id);
            for (var position = 0; position < series.Seasons.Count; position++)
            {
                var season = series.Seasons[position];
                insertSeason.Run(series.Id, position, season.RelativePath, season.ContentHash, season.ImageTag);
            }
        }
    }

    private static void WriteGroups(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<VersionGroup> groups)
    {
        StoreCommands.Execute(connection, transaction, "DELETE FROM stub_groups; DELETE FROM stub_group_stubs; DELETE FROM stub_group_local_items;");
        using var group = StoreCommands.Prepared(connection, transaction, "INSERT INTO stub_groups (position, id, identity_key, primary_stub_id) VALUES ($position, $id, $identity, $primary);", "$position", "$id", "$identity", "$primary");
        using var member = StoreCommands.Prepared(connection, transaction, "INSERT INTO stub_group_stubs (group_position, position, stub_id) VALUES ($group, $position, $stub);", "$group", "$position", "$stub");
        using var local = StoreCommands.Prepared(connection, transaction, "INSERT INTO stub_group_local_items (group_position, position, item_id) VALUES ($group, $position, $item);", "$group", "$position", "$item");
        for (var index = 0; index < groups.Count; index++)
        {
            var entry = groups[index];
            group.Run(index, entry.Id, entry.IdentityKey, entry.PrimaryStubId);
            for (var position = 0; position < entry.StubIds.Count; position++)
            {
                member.Run(index, position, entry.StubIds[position]);
            }

            for (var position = 0; position < entry.LocalItemIds.Count; position++)
            {
                local.Run(index, position, StoreCommands.Text(entry.LocalItemIds[position]));
            }
        }
    }

    private static void WriteNames(SqliteConnection connection, SqliteTransaction transaction, string clear, string insert, IReadOnlyDictionary<Guid, string> names)
    {
        StoreCommands.Execute(connection, transaction, clear);
        using var command = StoreCommands.Prepared(connection, transaction, insert, "$server", "$value");
        foreach (var (serverId, value) in names)
        {
            command.Run(StoreCommands.Text(serverId), value);
        }
    }

    private static Dictionary<string, List<StubSource>> ReadSources(SqliteConnection connection, SqliteTransaction transaction, string query)
    {
        var sources = new Dictionary<string, List<StubSource>>(StringComparer.Ordinal);
        using var command = StoreCommands.Command(connection, transaction, query);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            Add(sources, reader.GetString(0), new StubSource(reader.GetId(1), reader.GetString(2), reader.GetString(3)) { LibraryId = reader.GetTextOrNull(4) });
        }

        return sources;
    }

    private static List<VersionGroup> ReadGroups(SqliteConnection connection, SqliteTransaction transaction)
    {
        var members = new Dictionary<long, List<string>>();
        using (var command = StoreCommands.Command(connection, transaction, "SELECT group_position, stub_id FROM stub_group_stubs ORDER BY group_position, position;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                Add(members, reader.GetInt64(0), reader.GetString(1));
            }
        }

        var locals = new Dictionary<long, List<Guid>>();
        using (var command = StoreCommands.Command(connection, transaction, "SELECT group_position, item_id FROM stub_group_local_items ORDER BY group_position, position;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                Add(locals, reader.GetInt64(0), reader.GetId(1));
            }
        }

        var groups = new List<VersionGroup>();
        using (var command = StoreCommands.Command(connection, transaction, "SELECT position, id, identity_key, primary_stub_id FROM stub_groups ORDER BY position;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var position = reader.GetInt64(0);
                groups.Add(new VersionGroup
                {
                    Id = reader.GetString(1),
                    IdentityKey = reader.GetString(2),
                    PrimaryStubId = reader.GetTextOrNull(3),
                    StubIds = members.GetValueOrDefault(position) ?? [],
                    LocalItemIds = locals.GetValueOrDefault(position) ?? []
                });
            }
        }

        return groups;
    }

    private static Dictionary<Guid, string> ReadNames(SqliteConnection connection, SqliteTransaction transaction, string query)
    {
        var names = new Dictionary<Guid, string>();
        using var command = StoreCommands.Command(connection, transaction, query);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names[reader.GetId(0)] = reader.GetString(1);
        }

        return names;
    }

    private static List<Guid> ReadRemoved(SqliteConnection connection, SqliteTransaction transaction)
    {
        var removed = new List<Guid>();
        using var command = StoreCommands.Command(connection, transaction, "SELECT server_id FROM stub_removed_servers ORDER BY position;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            removed.Add(reader.GetId(0));
        }

        return removed;
    }

    private static List<StubRename> ReadRenames(SqliteConnection connection, SqliteTransaction transaction)
    {
        var renames = new List<StubRename>();
        using var command = StoreCommands.Command(connection, transaction, "SELECT stub_id, old_path, removed_item_id, forgets FROM stub_renames ORDER BY position;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            renames.Add(new StubRename(reader.GetString(0), reader.GetString(1)) { RemovedItemId = reader.GetIdOrNull(2), Forgets = reader.GetBoolean(3) });
        }

        return renames;
    }

    private static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> lists, TKey key, TValue value)
        where TKey : notnull
    {
        if (!lists.TryGetValue(key, out var list))
        {
            lists[key] = list = [];
        }

        list.Add(value);
    }

    /// <summary>
    /// A planned change of the registry.
    /// </summary>
    /// <param name="State">The state to store.</param>
    /// <param name="Rewrite">Whether the stubs and series in the tables are dropped first, because what they hold is not known.</param>
    internal sealed record StubWrite(StubState State, bool Rewrite)
    {
        /// <summary>Gets the stubs to add or replace, with their place.</summary>
        public List<(StubEntry Row, int Ordinal)> StubRows { get; } = [];

        /// <summary>Gets the stubs that only moved in the list.</summary>
        public List<(string Id, int Ordinal)> StubOrdinals { get; } = [];

        /// <summary>Gets the stubs to delete.</summary>
        public List<string> StubDeletes { get; } = [];

        /// <summary>Gets the series stubs to add or replace, with their place.</summary>
        public List<(SeriesStub Row, int Ordinal)> SeriesRows { get; } = [];

        /// <summary>Gets the series stubs that only moved in the list.</summary>
        public List<(string Id, int Ordinal)> SeriesOrdinals { get; } = [];

        /// <summary>Gets the series stubs to delete.</summary>
        public List<string> SeriesDeletes { get; } = [];

        /// <summary>Gets a value indicating whether the version groups are written again.</summary>
        public bool Groups { get; init; }

        /// <summary>Gets a value indicating whether the labels are written again.</summary>
        public bool Labels { get; init; }

        /// <summary>Gets a value indicating whether the server names are written again.</summary>
        public bool ServerNames { get; init; }

        /// <summary>Gets a value indicating whether the removal marks are written again.</summary>
        public bool RemovedServers { get; init; }

        /// <summary>Gets a value indicating whether the renames are written again.</summary>
        public bool Renames { get; init; }

        /// <summary>Gets a value indicating whether there is nothing to write.</summary>
        public bool IsEmpty => !Rewrite && !Groups && !Labels && !ServerNames && !RemovedServers && !Renames
            && StubRows.Count == 0 && StubOrdinals.Count == 0 && StubDeletes.Count == 0
            && SeriesRows.Count == 0 && SeriesOrdinals.Count == 0 && SeriesDeletes.Count == 0;
    }

    // Writes the sources of one stub or series stub again whole.
    private sealed class SourceWriter : IDisposable
    {
        private readonly SqliteCommand _clear;
        private readonly SqliteCommand _insert;

        public SourceWriter(SqliteConnection connection, SqliteTransaction transaction, string table, string owner)
        {
            // Only the two constant table names of this class come in here.
            _clear = StoreCommands.Prepared(connection, transaction, $"DELETE FROM {table} WHERE {owner} = $owner;", "$owner");
            _insert = StoreCommands.Prepared(
                connection,
                transaction,
                $"INSERT INTO {table} ({owner}, position, server_id, item_id, media_source_id, library_id) VALUES ($owner, $position, $server, $item, $source, $library);",
                "$owner",
                "$position",
                "$server",
                "$item",
                "$source",
                "$library");
        }

        public void Clear(string owner) => _clear.Run(owner);

        public void Replace(string owner, IReadOnlyList<StubSource> sources)
        {
            _clear.Run(owner);
            for (var position = 0; position < sources.Count; position++)
            {
                var source = sources[position];
                _insert.Run(owner, position, StoreCommands.Text(source.ServerId), source.ItemId, source.MediaSourceId, source.LibraryId);
            }
        }

        public void Dispose()
        {
            _clear.Dispose();
            _insert.Dispose();
        }
    }

    // Two version groups are the same when every field and every list match in order.
    private sealed class GroupComparer : IEqualityComparer<VersionGroup>
    {
        public static GroupComparer Instance { get; } = new();

        public bool Equals(VersionGroup? x, VersionGroup? y)
            => ReferenceEquals(x, y) || (x is not null && y is not null
                && string.Equals(x.Id, y.Id, StringComparison.Ordinal)
                && string.Equals(x.IdentityKey, y.IdentityKey, StringComparison.Ordinal)
                && string.Equals(x.PrimaryStubId, y.PrimaryStubId, StringComparison.Ordinal)
                && x.StubIds.SequenceEqual(y.StubIds, StringComparer.Ordinal)
                && x.LocalItemIds.SequenceEqual(y.LocalItemIds));

        public int GetHashCode(VersionGroup obj) => StringComparer.Ordinal.GetHashCode(obj.Id);
    }
}
