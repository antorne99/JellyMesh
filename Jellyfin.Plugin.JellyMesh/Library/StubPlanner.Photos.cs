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
/// Plans the photos of a server: a 1x1 placeholder with the real extension per remote photo and local library, in the
/// folders of the home videos, with the cover of its album as the folder image (D7, D45).
/// </summary>
public static partial class StubPlanner
{
    private const string AlbumCoverStem = "folder";

    // Plans one photo in each local library it goes to, and claims its stubs that still have its extension.
    private static List<StubWork> PhotoWorks(
        StubPlanInput input,
        IndexItemLookup catalog,
        Guid serverId,
        IndexedItem photo,
        Dictionary<(StubKind Kind, string ItemId, string Placement), StubEntry> existing,
        FolderNames names,
        HashSet<string> covered,
        HashSet<string> claimed,
        HashSet<string> unplaceable)
    {
        var works = new List<StubWork>();
        var placements = LibrariesOf(photo).GroupBy(library => input.PlacementOf(serverId, library, TypeFolder.HomeVideos), StringComparer.Ordinal).ToList();
        if (placements.Count == 0)
        {
            unplaceable.Add(StubKind.Photo + "|" + photo.ItemId);
        }

        foreach (var placement in placements)
        {
            var stub = existing.GetValueOrDefault((StubKind.Photo, photo.ItemId, placement.Key));
            if (stub is not null && !string.Equals(Path.GetExtension(stub.RelativePath), "." + photo.Extension, StringComparison.OrdinalIgnoreCase))
            {
                stub = null;
            }

            var library = placement.First();
            var id = stub?.Id ?? NewId();
            var relative = stub?.RelativePath
                ?? PhotoPath(catalog, serverId, photo, FederatedPaths.TypeFolderOf(serverId, library, TypeFolder.HomeVideos), names, input.IgnoreRules, id);
            works.Add(PhotoWork(input, catalog, serverId, photo, library, stub, id, relative, covered.Add(Path.GetDirectoryName(relative)!)));
            if (stub is not null)
            {
                claimed.Add(stub.Id);
            }
        }

        return works;
    }

    // The work of one photo placeholder; the photo that carries the cover of its folder writes it as the folder image.
    private static StubWork PhotoWork(StubPlanInput input, IndexItemLookup catalog, Guid serverId, IndexedItem photo, string library, StubEntry? stub, string id, string relative, bool carriesCover)
    {
        var entry = (stub ?? NewEntry(id, StubKind.Photo, relative)) with
        {
            ServerId = serverId,
            IdentityKey = string.Join('|', StubKind.Photo, serverId.ToString("N"), photo.ItemId),
            Sources = [new StubSource(serverId, photo.ItemId, photo.ItemId) { LibraryId = library }]
        };

        var metadata = StubMetadata.PhotoOf(photo, input.IsProtected, id);
        var stem = Path.Combine(Path.GetDirectoryName(relative)!, AlbumCoverStem);
        var images = new List<StubImage>();
        var imageTag = string.Empty;
        if (carriesCover && AlbumOf(catalog, serverId, photo) is { } album)
        {
            // The cover of an album is often a camera photo, so it is read like a thumbnail, with no EXIF (row 470).
            if (TagOf(album, ImageType.Primary) is not null)
            {
                images.Add(new StubImage(ImageType.Primary, stem, serverId, album.ItemId, null) { IsPhotoCover = true });
            }

            imageTag = ImageTagOf(album, images);
        }

        List<StubImage> removed = carriesCover && images.Count == 0 && !string.IsNullOrEmpty(entry.ImageTag)
            ? [new StubImage(ImageType.Primary, stem, null, null, null)]
            : [];
        // The technical tag of a photo is the remote Primary tag its thumbnail was made from.
        return new StubWork(entry, stub is null, metadata, string.Empty, string.Empty, string.Empty, Hash(JsonSerializer.Serialize(metadata), photo.Extension!), images, imageTag, photo.ImageTags.GetValueOrDefault("Primary") ?? string.Empty)
        {
            RemovedImages = removed
        };
    }

    private static IndexedItem? AlbumOf(IndexItemLookup catalog, Guid serverId, IndexedItem photo)
        => photo.ParentId is { } parentId && catalog.Find(serverId, parentId) is { Kind: IndexedItemKind.PhotoAlbum or IndexedItemKind.Folder } album ? album : null;

    // The path of a new photo: the folders of its album, then a file name Jellyfin reads as a photo of its own.
    private static string PhotoPath(IndexItemLookup catalog, Guid serverId, IndexedItem photo, string typeFolder, FolderNames names, IServerIgnoreRules rules, string id)
    {
        var folder = FolderOf(catalog, serverId, photo, typeFolder, rules);
        var name = FederatedPaths.Sanitize(photo.Name);
        var file = name + "." + photo.Extension;
        if (names.HidesPhoto(folder, file) || rules.Ignores(folder, file, false))
        {
            file = Marked(name);
            if (rules.Ignores(folder, file, false))
            {
                // The server skips the name for a part between two dots, such as ".sample.".
                file = Marked(name.Replace('.', ' '));
            }
        }

        names.AddPhoto(folder, file);
        return Path.Combine(folder, file);

        string Marked(string stem) => string.Create(CultureInfo.InvariantCulture, $"[{id[..8]}] {stem}.{photo.Extension}");
    }

    /// <summary>
    /// The home videos and photos of each folder. Jellyfin reads an image whose name starts with the name of a video of its
    /// folder, or with an artwork name, as artwork and not as a photo.
    /// </summary>
    private sealed class FolderNames
    {
        private readonly Dictionary<string, List<string>> _videos = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _photos = new(StringComparer.OrdinalIgnoreCase);

        public FolderNames(IEnumerable<StubEntry> stubs)
        {
            foreach (var stub in stubs)
            {
                var folder = Path.GetDirectoryName(stub.RelativePath)!;
                if (stub.Kind == StubKind.HomeVideo)
                {
                    AddVideo(folder, Path.GetFileNameWithoutExtension(stub.RelativePath));
                }
                else if (stub.Kind == StubKind.Photo)
                {
                    AddPhoto(folder, Path.GetFileName(stub.RelativePath));
                }
            }
        }

        public void AddVideo(string folder, string name) => ListOf(_videos, folder).Add(name);

        public void AddPhoto(string folder, string file) => ListOf(_photos, folder).Add(file);

        // Whether a home video name starts the file of a photo of its folder.
        public bool StartsAPhoto(string folder, string name)
            => _photos.TryGetValue(folder, out var files) && files.Exists(file => file.StartsWith(name, StringComparison.OrdinalIgnoreCase));

        // Whether Jellyfin would not read a photo file as a photo, or another photo of the folder has its file name.
        public bool HidesPhoto(string folder, string file)
            => Array.Exists(_artworkNames, word => file.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                || (_videos.TryGetValue(folder, out var videos) && videos.Exists(video => file.StartsWith(video, StringComparison.OrdinalIgnoreCase)))
                || (_photos.TryGetValue(folder, out var photos) && photos.Exists(other => string.Equals(other, file, StringComparison.OrdinalIgnoreCase)));

        private static List<string> ListOf(Dictionary<string, List<string>> lists, string folder)
        {
            if (!lists.TryGetValue(folder, out var list))
            {
                list = [];
                lists[folder] = list;
            }

            return list;
        }
    }
}
