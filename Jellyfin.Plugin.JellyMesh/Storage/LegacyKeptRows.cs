using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Library;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// The rows of the 0.5 <c>watch-state.db</c>, copied as they are into the tables of the kept watch state. Only the kind
/// changes form, from its number to its name.
/// </summary>
internal static class LegacyKeptRows
{
    private const string SelectTitles = """
        SELECT Id, OldItemId, Kind, Title, Year, Tmdb, Imdb, Tvdb, OriginalTitle, RunTimeTicks, Overview, Genres, Studios, People,
            SeasonNumber, EpisodeNumber, EpisodeNumberEnd, SavedUtc
        FROM Titles ORDER BY Id;
        """;

    private const string SelectUsers = """
        SELECT TitleId, UserId, Played, PlayCount, PlaybackPositionTicks, LastPlayedUtc, IsFavorite, Rating FROM UserStates;
        """;

    private const string InsertTitle = """
        INSERT OR REPLACE INTO kept_titles (old_item_id, kind, title, year, tmdb, imdb, tvdb, original_title, run_time_ticks, overview,
            genres, studios, people, season_number, episode_number, episode_number_end, saved_utc)
        VALUES ($old, $kind, $title, $year, $tmdb, $imdb, $tvdb, $original, $runtime, $overview, $genres, $studios, $people, $season,
            $episode, $episodeEnd, $saved)
        RETURNING id;
        """;

    private const string InsertUser = """
        INSERT OR REPLACE INTO kept_user_states (title_id, user_id, played, play_count, playback_position_ticks, last_played_utc,
            is_favorite, rating)
        VALUES ($title, $user, $played, $count, $position, $last, $favorite, $rating);
        """;

    /// <summary>
    /// Reads the titles with their user rows; a file without the tables reads as empty.
    /// </summary>
    /// <param name="connection">An open connection to the old file.</param>
    /// <returns>The titles.</returns>
    /// <exception cref="FormatException">A row holds an id or a kind that does not read.</exception>
    public static List<Title> Read(SqliteConnection connection)
    {
        using (var tables = connection.CreateCommand())
        {
            tables.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Titles', 'UserStates');";
            if ((long)tables.ExecuteScalar()! < 2)
            {
                return [];
            }
        }

        var users = new Dictionary<long, List<object?[]>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = SelectUsers;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new object?[reader.FieldCount];
                for (var column = 0; column < row.Length; column++)
                {
                    row[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);
                }

                row[1] = StoreSql.Text(Guid.Parse(reader.GetString(1)));
                if (!users.TryGetValue(reader.GetInt64(0), out var list))
                {
                    users[reader.GetInt64(0)] = list = [];
                }

                list.Add(row);
            }
        }

        var titles = new List<Title>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = SelectTitles;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var kind = reader.GetInt32(2);
                // The old versions knew only these three kinds.
                if (kind is < (int)KeptTitleKind.Movie or > (int)KeptTitleKind.Series)
                {
                    throw new FormatException("A kept title has an unknown kind.");
                }

                var row = new object?[reader.FieldCount - 1];
                for (var column = 1; column < reader.FieldCount; column++)
                {
                    row[column - 1] = reader.IsDBNull(column) ? null : reader.GetValue(column);
                }

                row[0] = StoreSql.Text(Guid.Parse(reader.GetString(1)));
                row[1] = ((KeptTitleKind)kind).ToString();
                titles.Add(new Title(row, users.GetValueOrDefault(reader.GetInt64(0)) ?? []));
            }
        }

        return titles;
    }

    /// <summary>
    /// Writes the titles and their user rows, inside the caller's transaction.
    /// </summary>
    /// <param name="connection">The writer connection.</param>
    /// <param name="transaction">The write transaction.</param>
    /// <param name="titles">The titles.</param>
    public static void Write(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<Title> titles)
    {
        string[] titleNames = ["$old", "$kind", "$title", "$year", "$tmdb", "$imdb", "$tvdb", "$original", "$runtime", "$overview", "$genres", "$studios", "$people", "$season", "$episode", "$episodeEnd", "$saved"];
        string[] userNames = ["$title", "$user", "$played", "$count", "$position", "$last", "$favorite", "$rating"];
        foreach (var title in titles)
        {
            long id;
            using (var command = StoreSql.Command(connection, transaction, InsertTitle, Pairs(titleNames, title.Row)))
            {
                id = (long)command.ExecuteScalar()!;
            }

            foreach (var user in title.Users)
            {
                var row = (object?[])user.Clone();
                row[0] = id;
                StoreSql.Execute(connection, transaction, InsertUser, Pairs(userNames, row));
            }
        }
    }

    private static (string Name, object? Value)[] Pairs(string[] names, object?[] values)
    {
        var pairs = new (string, object?)[names.Length];
        for (var index = 0; index < names.Length; index++)
        {
            pairs[index] = (names[index], values[index]);
        }

        return pairs;
    }

    /// <summary>
    /// A title row in the order of the new table, with its user rows.
    /// </summary>
    /// <param name="Row">The values of the title.</param>
    /// <param name="Users">The values of each user row, the title id first.</param>
    internal sealed record Title(object?[] Row, IReadOnlyList<object?[]> Users);
}
