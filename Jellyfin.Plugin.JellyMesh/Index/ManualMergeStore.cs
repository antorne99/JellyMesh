using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// <see cref="IManualMergeStore"/> over the tables <c>manual_merges</c> and <c>manual_merge_copies</c> of the plugin
/// database. A change reads the merges, changes them and writes only the merges that differ, in one short transaction.
/// While the database cannot be read the merges are not applied, as with the damaged file of 0.5, and read again once it can.
/// </summary>
public sealed class ManualMergeStore : IManualMergeStore, IDisposable
{
    /// <summary>
    /// The file of 0.5, which the data migration reads once.
    /// </summary>
    public const string FileName = "manual-merges.json";

    private const string SelectMerges = "SELECT id, created_utc FROM manual_merges ORDER BY created_utc, id;";

    private const string SelectCopies = """
        SELECT merge_id, server_id, item_id, kind, is_local, tmdb, imdb, tvdb, title, year
        FROM manual_merge_copies ORDER BY merge_id, position;
        """;

    private const string DeleteMerge = """
        DELETE FROM manual_merge_copies WHERE merge_id = $id;
        DELETE FROM manual_merges WHERE id = $id;
        """;

    private const string InsertMerge = "INSERT INTO manual_merges (id, created_utc) VALUES ($id, $created);";

    private const string InsertCopy = """
        INSERT INTO manual_merge_copies (merge_id, position, server_id, item_id, kind, is_local, tmdb, imdb, tvdb, title, year)
        VALUES ($merge, $position, $server, $item, $kind, $local, $tmdb, $imdb, $tvdb, $title, $year);
        """;

    private readonly IPluginDatabase _database;
    private readonly ILogger<ManualMergeStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Cached? _cache;
    private int _reported;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManualMergeStore"/> class.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    /// <param name="logger">The logger.</param>
    public ManualMergeStore(IPluginDatabase database, ILogger<ManualMergeStore> logger)
    {
        _database = database;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<ManualMerge> Merges => Load() ?? [];

    /// <inheritdoc />
    public bool IsDamaged => Load() is null;

    /// <inheritdoc />
    public async Task UpdateAsync(Func<IReadOnlyList<ManualMerge>, IReadOnlyList<ManualMerge>> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read in the write, so a change never starts from a copy older than the table.
            var next = await _database.WriteAsync(
                (connection, transaction) =>
                {
                    var current = ReadAll(connection, transaction);
                    var changed = change(current);
                    Replace(connection, transaction, current, changed);
                    return new Cached(_database.Generation, changed);
                },
                cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _cache, next);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private static List<ManualMerge> ReadAll(SqliteConnection connection, SqliteTransaction transaction)
    {
        var copies = new Dictionary<string, List<MergedCopy>>(StringComparer.Ordinal);
        using (var command = StoreSql.Command(connection, transaction, SelectCopies))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var mergeId = reader.GetString(0);
                if (!copies.TryGetValue(mergeId, out var list))
                {
                    copies[mergeId] = list = [];
                }

                list.Add(new MergedCopy
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
        }

        var merges = new List<ManualMerge>();
        using (var command = StoreSql.Command(connection, transaction, SelectMerges))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                merges.Add(new ManualMerge
                {
                    Id = Guid.ParseExact(id, "N"),
                    CreatedUtc = new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                    Copies = copies.GetValueOrDefault(id) ?? []
                });
            }
        }

        return merges;
    }

    // Deletes the merges gone or changed and inserts the new or changed ones; the others stay as they are.
    private static void Replace(SqliteConnection connection, SqliteTransaction transaction, List<ManualMerge> current, IReadOnlyList<ManualMerge> next)
    {
        var before = current.ToDictionary(merge => merge.Id);
        var after = next.DistinctBy(merge => merge.Id).ToDictionary(merge => merge.Id);
        foreach (var merge in current.Where(merge => !after.TryGetValue(merge.Id, out var kept) || !Same(merge, kept)))
        {
            StoreSql.Execute(connection, transaction, DeleteMerge, ("$id", StoreSql.Text(merge.Id)));
        }

        foreach (var merge in after.Values.Where(merge => !before.TryGetValue(merge.Id, out var old) || !Same(old, merge)))
        {
            var id = StoreSql.Text(merge.Id);
            StoreSql.Execute(connection, transaction, InsertMerge, ("$id", id), ("$created", merge.CreatedUtc.UtcTicks));
            for (var position = 0; position < merge.Copies.Count; position++)
            {
                var copy = merge.Copies[position];
                StoreSql.Execute(
                    connection,
                    transaction,
                    InsertCopy,
                    ("$merge", id),
                    ("$position", position),
                    ("$server", copy.ServerId),
                    ("$item", copy.ItemId),
                    ("$kind", copy.Kind.ToString()),
                    ("$local", copy.IsLocal),
                    ("$tmdb", copy.Tmdb),
                    ("$imdb", copy.Imdb),
                    ("$tvdb", copy.Tvdb),
                    ("$title", copy.Title),
                    ("$year", copy.Year));
            }
        }
    }

    private static bool Same(ManualMerge left, ManualMerge right) =>
        left.CreatedUtc.UtcTicks == right.CreatedUtc.UtcTicks && left.Copies.SequenceEqual(right.Copies);

    // The merges, read once the database can be read; null while it cannot.
    // The merges of the current opening of the database, read again after it is opened again; null while it cannot be read.
    private IReadOnlyList<ManualMerge>? Load()
    {
        var cached = Volatile.Read(ref _cache);
        if (cached is not null && cached.Generation == _database.Generation)
        {
            return cached.Merges;
        }

        try
        {
            var read = _database.ReadAsync(
                (connection, transaction) => new Cached(_database.Generation, ReadAll(connection, transaction)),
                CancellationToken.None).GetAwaiter().GetResult();

            // A change saved meanwhile is newer than this read.
            var loaded = Interlocked.CompareExchange(ref _cache, read, cached) == cached ? read : Volatile.Read(ref _cache)!;
            Volatile.Write(ref _reported, 0);
            return loaded.Merges;
        }
        catch (Exception exception) when (exception is StoreSuspendedException or SqliteException)
        {
            if (Interlocked.Exchange(ref _reported, 1) == 0)
            {
                _logger.LogError("Cannot read the manual merges: {Error}. They are not applied until the database reads", exception.GetType().Name);
            }

            return null;
        }
    }

    private sealed record Cached(long Generation, IReadOnlyList<ManualMerge> Merges);
}
