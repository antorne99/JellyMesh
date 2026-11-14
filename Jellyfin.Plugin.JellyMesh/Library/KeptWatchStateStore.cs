using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="IKeptWatchStateStore"/> over a SQLite file in the plugin data folder, through the Microsoft.Data.Sqlite that
/// Jellyfin itself loads (Q414). Every value goes in as a parameter. A damaged file is set aside and a new one made.
/// </summary>
public sealed class KeptWatchStateStore : IKeptWatchStateStore, IDisposable
{
    /// <summary>The file name.</summary>
    public const string FileName = "watch-state.db";

    private const int SchemaVersion = 1;

    // SQLITE_CORRUPT and SQLITE_NOTADB: the file is not a database Jellyfin's SQLite can read.
    private const int Corrupt = 11;
    private const int NotADatabase = 26;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS Titles (
            Id INTEGER PRIMARY KEY,
            OldItemId TEXT NOT NULL UNIQUE,
            Kind INTEGER NOT NULL,
            Title TEXT,
            Year INTEGER,
            Tmdb TEXT,
            Imdb TEXT,
            Tvdb TEXT,
            OriginalTitle TEXT,
            RunTimeTicks INTEGER,
            Overview TEXT,
            Genres TEXT NOT NULL,
            Studios TEXT NOT NULL,
            People TEXT NOT NULL,
            SeasonNumber INTEGER,
            EpisodeNumber INTEGER,
            EpisodeNumberEnd INTEGER,
            SavedUtc INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS UserStates (
            TitleId INTEGER NOT NULL,
            UserId TEXT NOT NULL,
            Played INTEGER NOT NULL,
            PlayCount INTEGER NOT NULL,
            PlaybackPositionTicks INTEGER NOT NULL,
            LastPlayedUtc INTEGER,
            IsFavorite INTEGER NOT NULL,
            Rating REAL,
            PRIMARY KEY (TitleId, UserId));
        PRAGMA user_version = 1;
        """;

    private const string SelectSavedOfOldItem = "SELECT SavedUtc FROM Titles WHERE OldItemId = $old;";

    private const string DeleteTitleOfOldItem = """
        DELETE FROM UserStates WHERE TitleId IN (SELECT Id FROM Titles WHERE OldItemId = $old);
        DELETE FROM Titles WHERE OldItemId = $old;
        """;

    private const string InsertTitle = """
        INSERT INTO Titles (OldItemId, Kind, Title, Year, Tmdb, Imdb, Tvdb, OriginalTitle, RunTimeTicks, Overview, Genres, Studios,
            People, SeasonNumber, EpisodeNumber, EpisodeNumberEnd, SavedUtc)
        VALUES ($old, $kind, $title, $year, $tmdb, $imdb, $tvdb, $original, $runtime, $overview, $genres, $studios, $people, $season,
            $episode, $episodeEnd, $saved)
        RETURNING Id;
        """;

    private const string InsertUser = """
        INSERT INTO UserStates (TitleId, UserId, Played, PlayCount, PlaybackPositionTicks, LastPlayedUtc, IsFavorite, Rating)
        VALUES ($title, $user, $played, $count, $position, $last, $favorite, $rating);
        """;

    private const string SelectTitles = """
        SELECT Id, OldItemId, Kind, Title, Year, Tmdb, Imdb, Tvdb, OriginalTitle, RunTimeTicks, Overview, Genres, Studios, People,
            SeasonNumber, EpisodeNumber, EpisodeNumberEnd, SavedUtc
        FROM Titles ORDER BY Id;
        """;

    private const string SelectUsers = """
        SELECT TitleId, UserId, Played, PlayCount, PlaybackPositionTicks, LastPlayedUtc, IsFavorite, Rating FROM UserStates;
        """;

    private const string DeleteUserRow = "DELETE FROM UserStates WHERE TitleId = $title AND UserId = $user;";

    private const string DeleteUser = "DELETE FROM UserStates WHERE UserId = $user;";

    private const string SelectUserIds = "SELECT DISTINCT UserId FROM UserStates;";

    private const string DeleteUsersSavedBefore = "DELETE FROM UserStates WHERE TitleId IN (SELECT Id FROM Titles WHERE SavedUtc < $before);";

    private const string DeleteTitlesSavedBefore = "DELETE FROM Titles WHERE SavedUtc < $before;";

    private const string DeleteEmptyTitles = "DELETE FROM Titles WHERE NOT EXISTS (SELECT 1 FROM UserStates WHERE UserStates.TitleId = Titles.Id);";

    private readonly string _path;
    private readonly string _connectionString;
    private readonly ILogger<KeptWatchStateStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _ready;

    /// <summary>
    /// Initializes a new instance of the <see cref="KeptWatchStateStore"/> class.
    /// </summary>
    /// <param name="folder">The plugin data folder.</param>
    /// <param name="logger">The logger.</param>
    public KeptWatchStateStore(string folder, ILogger<KeptWatchStateStore> logger)
    {
        _path = Path.Combine(folder, FileName);
        _logger = logger;

        // No pool: the server shares this assembly, and a pooled handle would keep the file open after each call.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    /// <inheritdoc />
    public bool Exists => File.Exists(_path);

    /// <inheritdoc />
    public Task SaveAsync(IReadOnlyCollection<KeptTitle> titles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(titles);
        return titles.Count == 0 ? Task.CompletedTask : RunAsync(
            (connection, transaction) =>
            {
                foreach (var title in titles)
                {
                    // A record saved again keeps its first date, so its 90 days are not reset.
                    var saved = SavedOf(connection, transaction, title.OldItemId) ?? ToUtc(title.SavedUtc).Ticks;
                    Execute(connection, transaction, DeleteTitleOfOldItem, ("$old", Text(title.OldItemId)));
                    var id = InsertTitleRow(connection, transaction, title, saved);
                    foreach (var user in title.Users)
                    {
                        Execute(
                            connection,
                            transaction,
                            InsertUser,
                            ("$title", id),
                            ("$user", Text(user.UserId)),
                            ("$played", user.Played),
                            ("$count", user.PlayCount),
                            ("$position", user.PlaybackPositionTicks),
                            ("$last", user.LastPlayedUtc is { } last ? ToUtc(last).Ticks : null),
                            ("$favorite", user.IsFavorite),
                            ("$rating", user.Rating));
                    }
                }

                return 0;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<KeptTitle>> ReadAsync(CancellationToken cancellationToken) => RunAsync(
        (connection, transaction) =>
        {
            var users = new Dictionary<long, List<KeptUserState>>();
            using (var command = Command(connection, transaction, SelectUsers))
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

            var titles = new List<KeptTitle>();
            using (var command = Command(connection, transaction, SelectTitles))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    titles.Add(ReadTitle(reader, users));
                }
            }

            return (IReadOnlyList<KeptTitle>)titles;
        },
        cancellationToken);

    /// <inheritdoc />
    public Task ForgetAsync(IReadOnlyCollection<(long TitleId, Guid UserId)> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Count == 0 ? Task.CompletedTask : RunAsync(
            (connection, transaction) =>
            {
                foreach (var (titleId, userId) in rows)
                {
                    Execute(connection, transaction, DeleteUserRow, ("$title", titleId), ("$user", Text(userId)));
                }

                return Execute(connection, transaction, DeleteEmptyTitles);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> PruneAsync(DateTime savedBeforeUtc, IReadOnlySet<Guid> users, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(users);
        return RunAsync(
            (connection, transaction) =>
            {
                // The user rows of old titles go first, so the count is of titles only.
                var before = ("$before", (object?)ToUtc(savedBeforeUtc).Ticks);
                Execute(connection, transaction, DeleteUsersSavedBefore, before);
                var removed = Execute(connection, transaction, DeleteTitlesSavedBefore, before);
                if (users.Count > 0)
                {
                    foreach (var gone in UserIds(connection, transaction).Where(user => !users.Contains(user)))
                    {
                        Execute(connection, transaction, DeleteUser, ("$user", Text(gone)));
                    }
                }

                return removed + Execute(connection, transaction, DeleteEmptyTitles);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private static string Text(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private static DateTime ToUtc(DateTime date) => date.Kind switch
    {
        DateTimeKind.Utc => date,
        DateTimeKind.Local => date.ToUniversalTime(),
        _ => DateTime.SpecifyKind(date, DateTimeKind.Utc)
    };

    private static string Json(IReadOnlyList<string> values) => JsonSerializer.Serialize(values);

    private static List<string> List(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every command text is a constant of this class; values go in as parameters.")]
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string text)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        return command;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction transaction, string text, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, text);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }

    private static long? SavedOf(SqliteConnection connection, SqliteTransaction transaction, Guid oldItemId)
    {
        using var command = Command(connection, transaction, SelectSavedOfOldItem);
        command.Parameters.AddWithValue("$old", Text(oldItemId));
        return command.ExecuteScalar() is long ticks ? ticks : null;
    }

    private static long InsertTitleRow(SqliteConnection connection, SqliteTransaction transaction, KeptTitle title, long savedTicks)
    {
        using var command = Command(connection, transaction, InsertTitle);
        foreach (var (name, value) in new (string, object?)[]
        {
            ("$old", Text(title.OldItemId)),
            ("$kind", (int)title.Kind),
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
            ("$saved", savedTicks)
        })
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return (long)command.ExecuteScalar()!;
    }

    private static List<Guid> UserIds(SqliteConnection connection, SqliteTransaction transaction)
    {
        var ids = new List<Guid>();
        using var command = Command(connection, transaction, SelectUserIds);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }

        return ids;
    }

    private static KeptUserState ReadUser(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(1)),
        reader.GetInt64(2) != 0,
        reader.GetInt32(3),
        reader.GetInt64(4),
        reader.IsDBNull(5) ? null : new DateTime(reader.GetInt64(5), DateTimeKind.Utc),
        reader.GetInt64(6) != 0,
        reader.IsDBNull(7) ? null : reader.GetDouble(7));

    private static KeptTitle ReadTitle(SqliteDataReader reader, Dictionary<long, List<KeptUserState>> users)
    {
        string? TextOrNull(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
        int? IntOrNull(int column) => reader.IsDBNull(column) ? null : reader.GetInt32(column);

        var id = reader.GetInt64(0);
        return new KeptTitle
        {
            Id = id,
            OldItemId = Guid.Parse(reader.GetString(1)),
            Kind = (KeptTitleKind)reader.GetInt32(2),
            Title = TextOrNull(3),
            Year = IntOrNull(4),
            Ids = new ContentIds(TextOrNull(5), TextOrNull(6), TextOrNull(7)),
            OriginalTitle = TextOrNull(8),
            RunTimeTicks = reader.IsDBNull(9) ? null : reader.GetInt64(9),
            Overview = TextOrNull(10),
            Genres = List(reader.GetString(11)),
            Studios = List(reader.GetString(12)),
            People = List(reader.GetString(13)),
            SeasonNumber = IntOrNull(14),
            EpisodeNumber = IntOrNull(15),
            EpisodeNumberEnd = IntOrNull(16),
            SavedUtc = new DateTime(reader.GetInt64(17), DateTimeKind.Utc),
            Users = users.GetValueOrDefault(id) ?? []
        };
    }

    // One connection and one transaction per call, one call at a time; the schema is made on first use.
    private async Task<T> RunAsync<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Run(work);
        }
        finally
        {
            _gate.Release();
        }
    }

    private T Run<T>(Func<SqliteConnection, SqliteTransaction, T> work)
    {
        if (!_ready)
        {
            EnsureSchema();
            _ready = true;
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var result = work(connection, transaction);
        transaction.Commit();
        return result;
    }

    private void EnsureSchema()
    {
        try
        {
            CreateSchema();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is Corrupt or NotADatabase)
        {
            var aside = _path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Move(_path, aside);
            _logger.LogWarning("The file of the watch state kept for removed servers was damaged. It was set aside and a new one made");
            CreateSchema();
        }
    }

    private void CreateSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var version = Command(connection, null, "PRAGMA user_version;");
        if (Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture) > SchemaVersion)
        {
            throw new InvalidDataException("The file of the watch state kept for removed servers is of a newer version.");
        }

        using var create = Command(connection, null, Schema);
        create.ExecuteNonQuery();
    }
}
