using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyMesh.Index;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Plans the track stubs of one server: one <c>.strm</c> per track and local library, with no <c>.nfo</c>, in
/// <c>Musica/&lt;album artist&gt;/&lt;album&gt;/</c> grouped by remote album, and one cover per album (D60).
/// </summary>
internal static class TrackStubPlanner
{
    /// <summary>The artist folder of a track with no artist.</summary>
    public const string UnknownArtist = "Unknown Artist";

    /// <summary>The album folder of a track with no album.</summary>
    public const string UnknownAlbum = "Unknown Album";

    private const string CoverStem = "folder";

    /// <summary>
    /// Plans the tracks of a server. Paths of stubs that exist never change; the tracks of the index claim their stubs by
    /// remote item, and the stubs left unclaimed go to the sweep.
    /// </summary>
    /// <param name="input">What is known.</param>
    /// <param name="catalog">The index, by item.</param>
    /// <param name="serverId">The server.</param>
    /// <param name="stubs">The track stubs of the server.</param>
    /// <returns>One work per track and local library.</returns>
    public static List<StubWork> Plan(StubPlanInput input, IndexItemLookup catalog, Guid serverId, IReadOnlyList<StubEntry> stubs)
    {
        // A federated track has no version of its own once the mapper drops the stub sources (no cycles).
        var tracks = input.Snapshot.Find(serverId)?.Items.Where(item => item.Kind == IndexedItemKind.Audio && item.Versions.Count > 0).ToList() ?? [];
        var albums = tracks.GroupBy(AlbumKeyOf, StringComparer.Ordinal).ToDictionary(group => group.Key, Album.Of, StringComparer.Ordinal);
        var keys = KeysOf(input.View, serverId);
        var paths = new Paths(serverId, stubs, input.PlacementOf, stub => stub.Sources.Select(source => catalog.Find(serverId, source.ItemId)).OfType<IndexedItem>().Select(AlbumKeyOf).FirstOrDefault());

        var works = new List<StubWork>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks.OrderBy(AlbumKeyOf, StringComparer.Ordinal).ThenBy(item => item.ParentIndexNumber ?? 1).ThenBy(item => item.IndexNumber ?? int.MaxValue).ThenBy(item => item.ItemId, StringComparer.Ordinal))
        {
            var album = albums[AlbumKeyOf(track)];
            foreach (var (placement, library) in PlacementsOf(input, serverId, track))
            {
                var stub = paths.Claim(placement, track.ItemId);
                var id = stub?.Id ?? Guid.NewGuid().ToString("N");
                var relative = stub?.RelativePath ?? paths.NewTrack(library, album, FileNameOf(track, album.HasDiscs), id);
                var folder = Path.GetDirectoryName(relative)!;
                var key = keys.GetValueOrDefault(track.ItemId) ?? string.Create(CultureInfo.InvariantCulture, $"Track|item:{serverId}:{track.ItemId}");
                works.Add(WorkOf(input, serverId, track, library, stub, id, relative, key, covered.Add(folder) ? album : null));
            }
        }

        return works;
    }

    // The key of the remote album of a track: its id, or else its artist and album name.
    private static string AlbumKeyOf(IndexedItem track) => track.AlbumId is { Length: > 0 } albumId
        ? "id:" + albumId
        : "name:" + ArtistOf(track.AlbumArtists, track.Artists) + "\n" + track.Album;

    private static string ArtistOf(IReadOnlyList<string>? albumArtists, IReadOnlyList<string>? artists)
        => albumArtists?.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? artists?.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? UnknownArtist;

    private static IEnumerable<(string Placement, string Library)> PlacementsOf(StubPlanInput input, Guid serverId, IndexedItem track)
        => track.LibraryIds.Where(FederatedPaths.IsLibraryId)
            .Select(library => (Placement: input.PlacementOf(serverId, library, TypeFolder.Music), Library: library))
            .DistinctBy(entry => entry.Placement, StringComparer.Ordinal);

    private static string FileNameOf(IndexedItem track, bool hasDiscs)
    {
        var title = FederatedPaths.Sanitize(track.Name);
        return track.IndexNumber is not { } number
            ? title
            : hasDiscs
                ? string.Create(CultureInfo.InvariantCulture, $"{track.ParentIndexNumber ?? 1}-{number:00} - {title}")
                : string.Create(CultureInfo.InvariantCulture, $"{number:00} - {title}");
    }

    // The dedup key of each track of the server (row 453); a track the view does not hold keeps a key of its own item.
    private static Dictionary<string, string> KeysOf(FederationView view, Guid serverId)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var track in view.Tracks)
        {
            foreach (var item in track.RemoteItems.Where(item => item.ServerId == serverId))
            {
                keys.TryAdd(item.ItemId, track.Identity.Key);
            }
        }

        return keys;
    }

    private static StubWork WorkOf(StubPlanInput input, Guid serverId, IndexedItem track, string library, StubEntry? stub, string id, string relative, string key, Album? cover)
    {
        var version = track.Versions[0];
        var entry = (stub ?? new StubEntry { Id = id, Kind = StubKind.Track, RelativePath = relative, Sources = [], IdentityKey = string.Empty }) with
        {
            ServerId = serverId,
            IdentityKey = key,
            Sources = [new StubSource(serverId, track.ItemId, version.MediaSourceId) { LibraryId = library }]
        };

        var metadata = StubMetadata.Track(track, input.IsProtected);
        var url = input.StrmUrl(id);
        var hash = StubPlanner.Hash(Fingerprint(metadata), url.Split('?', 2)[0]);
        var technical = string.Join('|', serverId.ToString("N"), track.ItemId, version.MediaSourceId, version.ETag, track.Etag);

        // One track of each album folder carries the cover, written as the folder image the album reads.
        var stem = Path.Combine(Path.GetDirectoryName(relative)!, CoverStem);
        List<StubImage> images = cover is { Id: { } albumId, CoverTag: not null } ? [new StubImage(ImageType.Primary, stem, serverId, albumId, null)] : [];
        var imageTag = images.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $"{ImageType.Primary}={cover!.CoverTag};") : string.Empty;
        var removed = new List<StubImage>();
        if (cover is not null && images.Count == 0 && !string.IsNullOrEmpty(entry.ImageTag))
        {
            removed.Add(new StubImage(ImageType.Primary, stem, null, null, null));
        }

        return new StubWork(entry, stub is null, metadata, string.Empty, string.Empty, url, hash, images, imageTag, technical) { RemovedImages = removed };
    }

    // Every field the plugin writes, so that a change of any of them reaches the item.
    private static string Fingerprint(StubMetadata metadata)
        => JsonSerializer.Serialize(metadata with { ProviderIds = new SortedDictionary<string, string>(metadata.ProviderIds.ToDictionary(), StringComparer.Ordinal) });

    // A remote album: its id, name, artist folder, cover and whether its tracks span more than one disc.
    private sealed record Album(string Key, string? Id, string Name, string Artist, string? CoverTag, bool HasDiscs)
    {
        public static Album Of(IGrouping<string, IndexedItem> tracks)
        {
            var first = tracks.First();
            var artist = tracks.Select(track => track.AlbumArtists?.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))).FirstOrDefault(name => name is not null)
                ?? ArtistOf(null, first.Artists);
            return new Album(
                tracks.Key,
                first.AlbumId is { Length: > 0 } id ? id : null,
                tracks.Select(track => track.Album).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? UnknownAlbum,
                artist,
                tracks.Select(track => track.AlbumPrimaryImageTag).FirstOrDefault(tag => !string.IsNullOrEmpty(tag)),
                tracks.Any(track => track.ParentIndexNumber > 1));
        }
    }

    // The stubs of the server by local library and remote item, and the folders and files already taken.
    private sealed class Paths
    {
        private readonly Guid _serverId;
        private readonly Dictionary<(string Placement, string ItemId), StubEntry> _byItem = [];
        private readonly Dictionary<(string TypeFolder, string Album), string> _albumFolders = [];
        private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);

        public Paths(Guid serverId, IReadOnlyList<StubEntry> stubs, Func<Guid, string, TypeFolder, string> placementOf, Func<StubEntry, string?> albumOf)
        {
            _serverId = serverId;
            foreach (var stub in stubs)
            {
                var folder = Path.GetDirectoryName(stub.RelativePath)!;
                _taken.Add(stub.RelativePath);
                _taken.Add(folder);
                if (FederatedPaths.RemoteLibraryOf(stub.RelativePath) is not { } library)
                {
                    continue;
                }

                foreach (var source in stub.Sources)
                {
                    _byItem.TryAdd((placementOf(serverId, library, TypeFolder.Music), source.ItemId), stub);
                }

                if (albumOf(stub) is { } album)
                {
                    _albumFolders.TryAdd((FederatedPaths.TypeFolderOf(serverId, library, TypeFolder.Music), album), folder);
                }
            }
        }

        public StubEntry? Claim(string placement, string itemId)
            => _byItem.TryGetValue((placement, itemId), out var stub) && _claimed.Add(stub.Id) ? stub : null;

        // The path of a new track: the folder of its album, made on first use, and a file name unique in it.
        public string NewTrack(string library, Album album, string fileName, string id)
        {
            var typeFolder = FederatedPaths.TypeFolderOf(_serverId, library, TypeFolder.Music);
            if (!_albumFolders.TryGetValue((typeFolder, album.Key), out var folder))
            {
                folder = Unique(Path.Combine(typeFolder, FederatedPaths.Sanitize(album.Artist), FederatedPaths.Sanitize(album.Name)), id);
                _albumFolders[(typeFolder, album.Key)] = folder;
            }

            var path = Path.Combine(folder, fileName + ".strm");
            return _taken.Add(path) ? path : Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{fileName} [{id[..8]}].strm"));
        }

        private string Unique(string folder, string id)
        {
            if (_taken.Add(folder))
            {
                return folder;
            }

            var unique = string.Create(CultureInfo.InvariantCulture, $"{folder} [{id[..8]}]");
            _taken.Add(unique);
            return unique;
        }
    }
}
