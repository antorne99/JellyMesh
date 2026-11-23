using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="IKeptWatchStateStore"/> over the tables <c>kept_titles</c>, <c>kept_user_states</c>, <c>kept_title_copies</c>
/// and <c>kept_merge_copies</c> of the plugin database. Every value goes in as a parameter; each call is one short
/// transaction.
/// </summary>
public sealed class KeptWatchStateStore : IKeptWatchStateStore
{
    /// <summary>The file of 0.5, which the data migration reads once.</summary>
    public const string FileName = "watch-state.db";

    private const string SelectAnyTitle = "SELECT EXISTS (SELECT 1 FROM kept_titles);";

    private const string SelectSavedOfOldItem = "SELECT saved_utc FROM kept_titles WHERE old_item_id = $old;";

    private const string DeleteTitleOfOldItem = """
        DELETE FROM kept_user_states WHERE title_id IN (SELECT id FROM kept_titles WHERE old_item_id = $old);
        DELETE FROM kept_title_copies WHERE title_id IN (SELECT id FROM kept_titles WHERE old_item_id = $old);
        DELETE FROM kept_titles WHERE old_item_id = $old;
        """;

    private const string InsertTitle = """
        INSERT INTO kept_titles (old_item_id, kind, title, year, tmdb, imdb, tvdb, original_title, run_time_ticks, overview, genres,
            studios, people, season_number, episode_number, episode_number_end, saved_utc)
        VALUES ($old, $kind, $title, $year, $tmdb, $imdb, $tvdb, $original, $runtime, $overview, $genres, $studios, $people, $season,
            $episode, $episodeEnd, $saved)
        RETURNING id;
        """;

    private const string InsertUser = """
        INSERT INTO kept_user_states (title_id, user_id, played, play_count, playback_position_ticks, last_played_utc, is_favorite,
            rating)
        VALUES ($title, $user, $played, $count, $position, $last, $favorite, $rating);
        """;

    private const string SelectTitles = """
        SELECT id, old_item_id, kind, title, year, tmdb, imdb, tvdb, original_title, run_time_ticks, overview, genres, studios, people,
            season_number, episode_number, episode_number_end, saved_utc
        FROM kept_titles ORDER BY id;
        """;

    private const string SelectUsers = """
        SELECT title_id, user_id, played, play_count, playback_position_ticks, last_played_utc, is_favorite, rating
        FROM kept_user_states;
        """;

    private const string DeleteUserRow = "DELETE FROM kept_user_states WHERE title_id = $title AND user_id = $user;";

    private const string DeleteUser = "DELETE FROM kept_user_states WHERE user_id = $user;";

    private const string SelectUserIds = "SELECT DISTINCT user_id FROM kept_user_states;";

    private const string DeleteUsersSavedBefore = "DELETE FROM kept_user_states WHERE title_id IN (SELECT id FROM kept_titles WHERE saved_utc < $before);";

    private const string DeleteTitlesSavedBefore = "DELETE FROM kept_titles WHERE saved_utc < $before;";

    private const string DeleteEmptyTitles = """
        DELETE FROM kept_titles WHERE NOT EXISTS (SELECT 1 FROM kept_user_states WHERE kept_user_states.title_id = kept_titles.id);
        """;

    private const string InsertTitleCopy = """
        INSERT INTO kept_title_copies (title_id, position, server_id, item_id, kind, is_local, tmdb, imdb, tvdb, title, year)
        VALUES ($owner, $position, $server, $item, $kind, $local, $tmdb, $imdb, $tvdb, $title, $year);
        """;

    private const string SelectTitleCopies = """
        SELECT title_id, server_id, item_id, kind, is_local, tmdb, imdb, tvdb, title, year
        FROM kept_title_copies ORDER BY title_id, position;
        """;

    private const string DeleteOrphanCopies = """
        DELETE FROM kept_title_copies WHERE NOT EXISTS (SELECT 1 FROM kept_titles WHERE kept_titles.id = kept_title_copies.title_id);
        """;

    private const string DeleteMergesOfServer = "DELETE FROM kept_merge_copies WHERE removed_server_id = $removed;";

    private const string InsertMergeCopy = """
        INSERT INTO kept_merge_copies (removed_server_id, merge_id, position, server_id, item_id, kind, is_local, tmdb, imdb, tvdb,
            title, year, saved_utc)
        VALUES ($removed, $owner, $position, $server, $item, $kind, $local, $tmdb, $imdb, $tvdb, $title, $year, $saved);
        """;

    private const string SelectMergeCopies = """
        SELECT removed_server_id || ':' || merge_id, server_id, item_id, kind, is_local, tmdb, imdb, tvdb, title, year, merge_id, saved_utc
        FROM kept_merge_copies ORDER BY removed_server_id, merge_id, position;
        """;

    private const string DeleteMergesSavedBefore = "DELETE FROM kept_merge_copies WHERE saved_utc < $before;";

    private readonly IPluginDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="KeptWatchStateStore"/> class.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    public KeptWatchStateStore(IPluginDatabase database)
    {
        _database = database;
    }

    /// <inheritdoc />
    public long Generation => _database.Generation;

    /// <inheritdoc />
    public Task<bool> HasTitlesAsync(CancellationToken cancellationToken) => _database.ReadAsync(
        (connection, transaction) =>
        {
            using var command = StoreSql.Command(connection, transaction, SelectAnyTitle);
            return (long)command.ExecuteScalar()! != 0;
        },
        cancellationToken);

    /// <inheritdoc />
    public Task SaveAsync(IReadOnlyCollection<KeptTitle> titles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(titles);
        return titles.Count == 0 ? Task.CompletedTask : _database.WriteAsync(
            (connection, transaction) =>
            {
                foreach (var title in titles)
                {
                    // A record saved again keeps its first date, so its 90 days are not reset.
                    var saved = SavedOf(connection, transaction, title.OldItemId) ?? ToUtc(title.SavedUtc).Ticks;
                    StoreSql.Execute(connection, transaction, DeleteTitleOfOldItem, ("$old", StoreSql.Text(title.OldItemId)));
                    var id = InsertTitleRow(connection, transaction, title, saved);
                    InsertCopies(connection, transaction, InsertTitleCopy, title.Copies, ("$owner", id));
                    foreach (var user in title.Users)
                    {
                        StoreSql.Execute(
                            connection,
                            transaction,
                            InsertUser,
                            ("$title", id),
                            ("$user", StoreSql.Text(user.UserId)),
                            ("$played", user.Played),
                            ("$count", user.PlayCount),
                            ("$position", user.PlaybackPositionTicks),
                            ("$last", user.LastPlayedUtc is { } last ? ToUtc(last).Ticks : null),
                            ("$favorite", user.IsFavorite),
                            ("$rating", user.Rating));
                    }
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<KeptTitle>> ReadAsync(CancellationToken cancellationToken) => _database.ReadAsync(
        (connection, transaction) =>
        {
            var users = new Dictionary<long, List<KeptUserState>>();
            using (var command = StoreSql.Command(connection, transaction, SelectUsers))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var titleId = reader.GetInt64(0);
                    if (!users.TryGetValue(titleId, out var list))
                    {
                        users[titleId] = list = [];
                    }

                    list.Add(ReadUser(reader));
                }
            }

            var copies = ReadCopies(connection, transaction, SelectTitleCopies, reader => reader.GetInt64(0));
            var titles = new List<KeptTitle>();
            using (var command = StoreSql.Command(connection, transaction, SelectTitles))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var title = ReadTitle(reader, users);
                    titles.Add(title with { Copies = copies.TryGetValue(title.Id, out var own) ? own.Copies : [] });
                }
            }

            return (IReadOnlyList<KeptTitle>)titles;
        },
        cancellationToken);

    /// <inheritdoc />
    public Task ForgetAsync(IReadOnlyCollection<(long TitleId, Guid UserId)> rows, long generation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Count == 0 ? Task.CompletedTask : _database.WriteAsync(
            (connection, transaction) =>
            {
                if (_database.Generation != generation)
                {
                    throw new StoreSuspendedException("The database was opened again since the kept titles were read.");
                }

                foreach (var (titleId, userId) in rows)
                {
                    StoreSql.Execute(connection, transaction, DeleteUserRow, ("$title", titleId), ("$user", StoreSql.Text(userId)));
                }

                StoreSql.Execute(connection, transaction, DeleteEmptyTitles);
                StoreSql.Execute(connection, transaction, DeleteOrphanCopies);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> PruneAsync(DateTime savedBeforeUtc, IReadOnlySet<Guid> users, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(users);
        return _database.WriteAsync(
            (connection, transaction) =>
            {
                // The user rows of old titles go first, so the count is of titles only.
                var before = ("$before", (object?)ToUtc(savedBeforeUtc).Ticks);
                StoreSql.Execute(connection, transaction, DeleteUsersSavedBefore, before);
                var removed = StoreSql.Execute(connection, transaction, DeleteTitlesSavedBefore, before);
                if (users.Count > 0)
                {
                    foreach (var gone in UserIds(connection, transaction).Where(user => !users.Contains(user)))
                    {
                        StoreSql.Execute(connection, transaction, DeleteUser, ("$user", StoreSql.Text(gone)));
                    }
                }

                removed += StoreSql.Execute(connection, transaction, DeleteEmptyTitles);
                StoreSql.Execute(connection, transaction, DeleteOrphanCopies);
                StoreSql.Execute(connection, transaction, DeleteMergesSavedBefore, before);
                return removed;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task SaveMergesAsync(Guid serverId, IReadOnlyList<ManualMerge> merges, DateTime savedUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(merges);
        return _database.WriteAsync(
            (connection, transaction) =>
            {
                var removed = ("$removed", (object?)StoreSql.Text(serverId));
                StoreSql.Execute(connection, transaction, DeleteMergesOfServer, removed);
                foreach (var merge in merges)
                {
                    InsertCopies(connection, transaction, InsertMergeCopy, merge.Copies, removed, ("$owner", StoreSql.Text(merge.Id)), ("$saved", ToUtc(savedUtc).Ticks));
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<(Guid ServerId, ManualMerge Merge)>> ReadMergesAsync(CancellationToken cancellationToken) => _database.ReadAsync(
        (connection, transaction) => (IReadOnlyList<(Guid, ManualMerge)>)ReadCopies(connection, transaction, SelectMergeCopies, reader => reader.GetString(0))
            .Select(entry => (
                Guid.ParseExact(entry.Key.Split(':')[0], "N"),
                new ManualMerge { Id = entry.Value.MergeId, CreatedUtc = entry.Value.SavedUtc, Copies = entry.Value.Copies }))
            .ToList(),
        cancellationToken);

    private static void InsertCopies(SqliteConnection connection, SqliteTransaction transaction, string sql, IReadOnlyList<MergedCopy> copies, params (string Name, object? Value)[] owner)
    {
        for (var position = 0; position < copies.Count; position++)
        {
            var copy = copies[position];
            StoreSql.Execute(
                connection,
                transaction,
                sql,
                [
                    .. owner,
                    ("$position", position),
                    ("$server", copy.ServerId),
                    ("$item", copy.ItemId),
                    ("$kind", copy.Kind.ToString()),
                    ("$local", copy.IsLocal),
                    ("$tmdb", copy.Tmdb),
                    ("$imdb", copy.Imdb),
                    ("$tvdb", copy.Tvdb),
                    ("$title", copy.Title),
                    ("$year", copy.Year)
                ]);
        }
    }

    // The copies by owner, in order. A merge row also gives its id and date.
    private static Dictionary<TKey, (Guid MergeId, DateTimeOffset SavedUtc, List<MergedCopy> Copies)> ReadCopies<TKey>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        Func<SqliteDataReader, TKey> ownerOf)
        where TKey : notnull
    {
        var owners = new Dictionary<TKey, (Guid, DateTimeOffset, List<MergedCopy> Copies)>();
        using var command = StoreSql.Command(connection, transaction, sql);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var owner = ownerOf(reader);
            if (!owners.TryGetValue(owner, out var entry))
            {
                var isMerge = reader.FieldCount > 10;
                entry = (
                    isMerge ? Guid.ParseExact(reader.GetString(10), "N") : Guid.Empty,
                    isMerge ? new DateTimeOffset(reader.GetInt64(11), TimeSpan.Zero) : default,
                    []);
                owners[owner] = entry;
            }

            entry.Copies.Add(new MergedCopy
            {
                ServerId = reader.GetString(1),
                ItemId = reader.GetString(2),
                Kind = Enum.TryParse<ContentKind>(reader.GetString(3), out var kind) ? kind : ContentKind.Movie,
                IsLocal = reader.GetInt64(4) != 0,
                Tmdb = StoreSql.TextOrNull(reader, 5),
                Imdb = StoreSql.TextOrNull(reader, 6),
                Tvdb = StoreSql.TextOrNull(reader, 7),
                Title = StoreSql.TextOrNull(reader, 8),
                Year = StoreSql.IntOrNull(reader, 9)
            });
        }

        return owners;
    }

    private static DateTime ToUtc(DateTime date) => date.Kind switch
    {
        DateTimeKind.Utc => date,
        DateTimeKind.Local => date.ToUniversalTime(),
        _ => DateTime.SpecifyKind(date, DateTimeKind.Utc)
    };

    private static string Json(IReadOnlyList<string> values) => JsonSerializer.Serialize(values);

    private static List<string> List(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static long? SavedOf(SqliteConnection connection, SqliteTransaction transaction, Guid oldItemId)
    {
        using var command = StoreSql.Command(connection, transaction, SelectSavedOfOldItem, ("$old", StoreSql.Text(oldItemId)));
        return command.ExecuteScalar() is long ticks ? ticks : null;
    }

    private static long InsertTitleRow(SqliteConnection connection, SqliteTransaction transaction, KeptTitle title, long savedTicks)
    {
        using var command = StoreSql.Command(
            connection,
            transaction,
            InsertTitle,
            ("$old", StoreSql.Text(title.OldItemId)),
            ("$kind", title.Kind.ToString()),
            ("$title", title.Title),
            ("$year", title.Year),
            ("$tmdb", title.Ids.Tmdb),
            ("$imdb", title.Ids.Imdb),
            ("$tvdb", title.Ids.Tvdb),
            ("$original", title.OriginalTitle),
            ("$runtime", title.RunTimeTicks),
            ("$overview", title.Overview),
            ("$genres", Json(title.Genres)),
            ("$studios", Json(title.Studios)),
            ("$people", Json(title.People)),
            ("$season", title.SeasonNumber),
            ("$episode", title.EpisodeNumber),
            ("$episodeEnd", title.EpisodeNumberEnd),
            ("$saved", savedTicks));
        return (long)command.ExecuteScalar()!;
    }

    private static List<Guid> UserIds(SqliteConnection connection, SqliteTransaction transaction)
    {
        var ids = new List<Guid>();
        using var command = StoreSql.Command(connection, transaction, SelectUserIds);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(Guid.ParseExact(reader.GetString(0), "N"));
        }

        return ids;
    }

    private static KeptUserState ReadUser(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(1), "N"),
        reader.GetInt64(2) != 0,
        reader.GetInt32(3),
        reader.GetInt64(4),
        StoreSql.LongOrNull(reader, 5) is { } last ? new DateTime(last, DateTimeKind.Utc) : null,
        reader.GetInt64(6) != 0,
        reader.IsDBNull(7) ? null : reader.GetDouble(7));

    private static KeptTitle ReadTitle(SqliteDataReader reader, Dictionary<long, List<KeptUserState>> users)
    {
        var id = reader.GetInt64(0);
        return new KeptTitle
        {
            Id = id,
            OldItemId = Guid.ParseExact(reader.GetString(1), "N"),
            Kind = Enum.Parse<KeptTitleKind>(reader.GetString(2)),
            Title = StoreSql.TextOrNull(reader, 3),
            Year = StoreSql.IntOrNull(reader, 4),
            Ids = new ContentIds(StoreSql.TextOrNull(reader, 5), StoreSql.TextOrNull(reader, 6), StoreSql.TextOrNull(reader, 7)),
            OriginalTitle = StoreSql.TextOrNull(reader, 8),
            RunTimeTicks = StoreSql.LongOrNull(reader, 9),
            Overview = StoreSql.TextOrNull(reader, 10),
            Genres = List(reader.GetString(11)),
            Studios = List(reader.GetString(12)),
            People = List(reader.GetString(13)),
            SeasonNumber = StoreSql.IntOrNull(reader, 14),
            EpisodeNumber = StoreSql.IntOrNull(reader, 15),
            EpisodeNumberEnd = StoreSql.IntOrNull(reader, 16),
            SavedUtc = new DateTime(reader.GetInt64(17), DateTimeKind.Utc),
            Users = users.GetValueOrDefault(id) ?? []
        };
    }
}
