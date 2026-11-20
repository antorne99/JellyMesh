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
    /// Works out one decision per track identity, whatever the order of the input. Copies with the same MusicBrainz
    /// recording or track ID are one track; otherwise copies with the same album artist, album, disc, number and normalized
    /// title, and durations within 2 s, are one. Different recording IDs always keep two copies apart.
    /// </summary>
    /// <param name="tracks">The tracks of every remote server.</param>
    /// <returns>The decisions, sorted by key: the best copy as winner, every other copy best first as backups.</returns>
    public static IReadOnlyList<FederationDecision> Resolve(IEnumerable<TrackItem> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var items = tracks
            .Where(track => track.Versions.Count > 0)
            .DistinctBy(track => (track.ServerId, track.ItemId))
            .OrderBy(track => track.ServerId, StringComparer.Ordinal)
            .ThenBy(track => track.ItemId, StringComparer.Ordinal)
            .ToList();
        var sets = new Components(items);

        JoinBy(items, sets, track => track.RecordingId);
        JoinBy(items, sets, track => track.TrackId);
        JoinByPosition(items, sets);

        return Enumerable.Range(0, items.Count)
            .GroupBy(sets.Find)
            .Select(group => Decide(group.Select(index => items[index]).ToList()))
            .OrderBy(decision => decision.Identity.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static void JoinBy(List<TrackItem> items, Components sets, Func<TrackItem, string?> idOf)
    {
        var first = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < items.Count; index++)
        {
            if (Id(idOf(items[index])) is not { } id)
            {
                continue;
            }

            if (first.TryGetValue(id, out var other))
            {
                sets.Union(other, index);
            }
            else
            {
                first[id] = index;
            }
        }
    }

    // Copies in the same place of the same album, joined when their durations are within 2 s and their recordings agree.
    private static void JoinByPosition(List<TrackItem> items, Components sets)
    {
        var buckets = Enumerable.Range(0, items.Count)
            .Select(index => (Index: index, Key: PositionOf(items[index])))
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

    // The normalized album artists, album, disc, number and title, or null when one is missing or there is no duration.
    private static string? PositionOf(TrackItem track)
    {
        var artists = track.AlbumArtists.Any(name => !string.IsNullOrWhiteSpace(name)) ? track.AlbumArtists : track.Artists;
        var artist = string.Join('\t', artists.Select(name => TitleNormalizer.Normalize(name, null)).Where(name => name.Length > 0));
        var album = TitleNormalizer.Normalize(track.Album, null);
        var title = TitleNormalizer.Normalize(track.Title, null);
        if (artist.Length == 0 || album.Length == 0 || title.Length == 0 || track.TrackNumber is not { } number || track.RunTimeTicks is null)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{artist}\n{album}\n{track.DiscNumber ?? 1}\n{number}\n{title}");
    }

    private static FederationDecision Decide(List<TrackItem> copies)
    {
        var ranked = QualityRanking.RankAudio(copies.SelectMany(copy => copy.ToSources()));
        var remoteItems = copies
            .Select(copy => new RemoteItemKey(copy.ServerId, copy.ItemId))
            .OrderBy(item => item.ServerId, StringComparer.Ordinal)
            .ThenBy(item => item.ItemId, StringComparer.Ordinal)
            .ToList();
        return new FederationDecision(IdentityOf(copies), ranked[0], ranked.Skip(1).ToList()) { RemoteItems = remoteItems };
    }

    // The key of a group: its lowest recording ID, else its lowest track ID, else its place in the album, else its item.
    private static ContentIdentity IdentityOf(List<TrackItem> copies)
    {
        var recording = LowestOf(copies, track => track.RecordingId);
        var trackId = LowestOf(copies, track => track.TrackId);
        var position = copies.Select(PositionOf).OfType<string>().Order(StringComparer.Ordinal).FirstOrDefault();
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

    // Union-find over the tracks, keeping the recording IDs of each group so that two recordings never meet.
    private sealed class Components
    {
        private readonly int[] _parent;
        private readonly HashSet<string>?[] _recordings;

        public Components(List<TrackItem> items)
        {
            _parent = Enumerable.Range(0, items.Count).ToArray();
            _recordings = items
                .Select(item => Id(item.RecordingId) is { } id ? new HashSet<string>([id], StringComparer.OrdinalIgnoreCase) : null)
                .ToArray();
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

        public void Union(int left, int right)
        {
            var (a, b) = (Find(left), Find(right));
            if (a == b)
            {
                return;
            }

            var (root, child) = a < b ? (a, b) : (b, a);
            _parent[child] = root;
            if (_recordings[child] is { } moved)
            {
                (_recordings[root] ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).UnionWith(moved);
            }
        }

        public void UnionIfRecordingsAgree(int left, int right)
        {
            var (a, b) = (_recordings[Find(left)], _recordings[Find(right)]);
            if (a is null || b is null || a.Overlaps(b))
            {
                Union(left, right);
            }
        }
    }
}
