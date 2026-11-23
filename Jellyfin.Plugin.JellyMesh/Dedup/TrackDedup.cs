using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// Groups the remote copies of each track and ranks them (row 453, D65). Pure and stateless. A local copy plays no part:
/// a federated track never turns to it (row 64 for music).
/// </summary>
public static class TrackDedup
{
    private static readonly long _durationTolerance = TimeSpan.FromSeconds(2).Ticks;

    /// <summary>
    /// Works out one decision per track, whatever the input order: by MusicBrainz recording or track ID, else by album place
    /// with durations within 2 s. Different recording IDs always keep two copies apart.
    /// </summary>
    /// <param name="tracks">The tracks of every remote server.</param>
    /// <returns>The decisions, sorted by key: the best copy as winner, every other copy best first as backups.</returns>
    public static IReadOnlyList<FederationDecision> Resolve(IEnumerable<TrackItem> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var items = Sorted(tracks.Where(track => track.Versions.Count > 0));
        var (sets, positions) = Group(items);

        return Enumerable.Range(0, items.Count)
            .GroupBy(sets.Find)
            .Select(group => Decide(group.ToList(), items, positions))
            .OrderBy(decision => decision.Identity.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Pairs each saved track with every current copy of it, by the rules of <see cref="Resolve"/>, whatever their versions.
    /// </summary>
    /// <param name="saved">The saved tracks.</param>
    /// <param name="current">The current tracks.</param>
    /// <returns>The pairs.</returns>
    public static IReadOnlyList<(TrackItem Saved, TrackItem Current)> Pair(IReadOnlyCollection<TrackItem> saved, IReadOnlyCollection<TrackItem> current)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(current);
        if (saved.Count == 0 || current.Count == 0)
        {
            return [];
        }

        var items = Sorted(current);
        var copies = items.Count;
        items.AddRange(saved);
        var sets = Group(items).Sets;
        var copiesOf = Enumerable.Range(0, copies).ToLookup(sets.Find);
        return Enumerable.Range(copies, items.Count - copies)
            .SelectMany(kept => copiesOf[sets.Find(kept)].Select(copy => (items[kept], items[copy])))
            .ToList();
    }

    private static List<TrackItem> Sorted(IEnumerable<TrackItem> tracks) => tracks
        .DistinctBy(track => (track.ServerId, track.ItemId))
        .OrderBy(track => track.ServerId, StringComparer.Ordinal)
        .ThenBy(track => track.ItemId, StringComparer.Ordinal)
        .ToList();

    // The groups of the copies of one track, with the album place of each copy.
    private static (Components Sets, string?[] Positions) Group(List<TrackItem> items)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var positions = items.Select(item => PositionOf(item, item.TrackNumber, names)).ToArray();
        var sets = new Components(items);
        JoinBy(items, sets, track => track.RecordingId);
        JoinBy(items, sets, track => track.TrackId);
        JoinByPosition(items, positions, sets);

        // The only files of two audiobooks need no number (row 535).
        JoinByPosition(items, items.Select(item => item.IsOnlyFile ? PositionOf(item, 0, names) : null).ToArray(), sets);
        return (sets, positions);
    }

    // Joins the copies sharing an ID, unless their groups hold different recordings.
    private static void JoinBy(List<TrackItem> items, Components sets, Func<TrackItem, string?> idOf)
    {
        var holders = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < items.Count; index++)
        {
            if (Id(idOf(items[index])) is not { } id)
            {
                continue;
            }

            if (!holders.TryGetValue(id, out var earlier))
            {
                earlier = [];
                holders[id] = earlier;
            }

            foreach (var other in earlier)
            {
                sets.UnionIfRecordingsAgree(other, index);
            }

            earlier.Add(index);
        }
    }

    // Copies in the same place of the same album, joined when their durations are within 2 s and their recordings agree.
    private static void JoinByPosition(List<TrackItem> items, string?[] positions, Components sets)
    {
        var buckets = Enumerable.Range(0, items.Count)
            .Select(index => (Index: index, Key: positions[index]))
            .Where(entry => entry.Key is not null)
            .GroupBy(entry => entry.Key!, entry => entry.Index, StringComparer.Ordinal)
            .OrderBy(bucket => bucket.Key, StringComparer.Ordinal);
        foreach (var bucket in buckets)
        {
            var sorted = bucket.OrderBy(index => items[index].RunTimeTicks).ThenBy(index => index).ToList();
            for (var left = 0; left < sorted.Count; left++)
            {
                for (var right = left + 1; right < sorted.Count && items[sorted[right]].RunTimeTicks - items[sorted[left]].RunTimeTicks <= _durationTolerance; right++)
                {
                    sets.UnionIfRecordingsAgree(sorted[left], sorted[right]);
                }
            }
        }
    }

    // The normalized album artists, album, disc, number and title, or null when one is missing or there is no real duration.
    private static string? PositionOf(TrackItem track, int? trackNumber, Dictionary<string, string> names)
    {
        var artists = track.AlbumArtists.Any(name => !string.IsNullOrWhiteSpace(name)) ? track.AlbumArtists : track.Artists;
        var artist = string.Join('\t', artists.Select(name => Normalized(name, names)).Where(name => name.Length > 0));
        var album = Normalized(track.Album, names);
        var title = TitleNormalizer.Normalize(track.Title, null);
        if (artist.Length == 0 || album.Length == 0 || title.Length == 0 || trackNumber is not { } number || track.RunTimeTicks is not > 0)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{artist}\n{album}\n{track.DiscNumber ?? 1}\n{number}\n{title}");
    }

    // Artist and album names repeat across the tracks of an album, so each is normalized once.
    private static string Normalized(string? name, Dictionary<string, string> names)
    {
        if (name is null)
        {
            return string.Empty;
        }

        if (!names.TryGetValue(name, out var normalized))
        {
            normalized = TitleNormalizer.Normalize(name, null);
            names[name] = normalized;
        }

        return normalized;
    }

    private static FederationDecision Decide(List<int> indexes, List<TrackItem> items, string?[] positions)
    {
        var copies = indexes.Select(index => items[index]).ToList();
        var sources = copies.SelectMany(copy => copy.ToSources()).ToList();
        var ranked = sources.Count == 1 ? sources : QualityRanking.RankAudio(sources);
        var remoteItems = copies
            .Select(copy => new RemoteItemKey(copy.ServerId, copy.ItemId))
            .OrderBy(item => item.ServerId, StringComparer.Ordinal)
            .ThenBy(item => item.ItemId, StringComparer.Ordinal)
            .ToList();
        var position = indexes.Select(index => positions[index]).OfType<string>().Order(StringComparer.Ordinal).FirstOrDefault();
        return new FederationDecision(IdentityOf(copies, position), ranked[0], ranked.Skip(1).ToList()) { RemoteItems = remoteItems };
    }

    // The key of a group: its lowest recording ID, else its lowest track ID, else its place in the album, else its item.
    private static ContentIdentity IdentityOf(List<TrackItem> copies, string? position)
    {
        var recording = LowestOf(copies, track => track.RecordingId);
        var trackId = LowestOf(copies, track => track.TrackId);
        var first = copies[0];
        var (key, match) = (recording, trackId, position) switch
        {
            ({ } id, _, _) => ("Track|mbr:" + id, MatchKind.ProviderId),
            (null, { } id, _) => ("Track|mbt:" + id, MatchKind.ProviderId),
            (null, null, { } place) when copies.Count > 1 => (
                string.Create(CultureInfo.InvariantCulture, $"Track|tags:{place}\n{copies.Min(track => track.RunTimeTicks)}"),
                MatchKind.AlbumPosition),
            _ => (string.Create(CultureInfo.InvariantCulture, $"{ContentKind.Track}|item:{first.ServerId}:{first.ItemId}"), MatchKind.ItemOnly),
        };

        return new ContentIdentity(key, key, ContentKind.Track, match, first.DiscNumber, first.TrackNumber);
    }

    private static string? LowestOf(List<TrackItem> copies, Func<TrackItem, string?> idOf)
        => copies.Select(copy => Id(idOf(copy))?.ToLowerInvariant()).OfType<string>().Order(StringComparer.Ordinal).FirstOrDefault();

    private static string? Id(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Union-find over the tracks, keeping the recording ID of each group so that two recordings never meet.
    private sealed class Components
    {
        private readonly int[] _parent;
        private readonly string?[] _recordings;

        public Components(List<TrackItem> items)
        {
            _parent = Enumerable.Range(0, items.Count).ToArray();
            _recordings = items.Select(item => Id(item.RecordingId)).ToArray();
        }

        public int Find(int index)
        {
            while (_parent[index] != index)
            {
                _parent[index] = _parent[_parent[index]];
                index = _parent[index];
            }

            return index;
        }

        // Every join goes through here, so a group holds at most one recording.
        public void UnionIfRecordingsAgree(int left, int right)
        {
            var (a, b) = (Find(left), Find(right));
            var (first, second) = (_recordings[a], _recordings[b]);
            if (a == b || (first is not null && second is not null && !string.Equals(first, second, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var (root, child) = a < b ? (a, b) : (b, a);
            _parent[child] = root;
            _recordings[root] ??= _recordings[child];
        }
    }
}
