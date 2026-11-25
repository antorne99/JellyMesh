using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Emby.Naming.Common;
using Emby.Naming.Video;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Plans the home videos and photos of a server: one stub per remote item and local library, read from the index. Music
/// videos are titles, planned like films (row 454).
/// </summary>
public static partial class StubPlanner
{
    // Deeper folder chains are cut there, so that a damaged index cannot make paths without end.
    private const int MaxFolderDepth = 32;

    private const string ThumbSuffix = "-thumb";

    // A plain file name, to ask whether the server skips every file of a folder.
    private const string ProbeFileName = "Video.strm";

    // The extensions of the files written next to a home video.
    private static readonly HashSet<string> _fileExtensions = new([".strm", ".nfo", ".jpg", ".png", ".webp", ".gif"], StringComparer.OrdinalIgnoreCase);

    // Jellyfin's own naming rules, which tell an extra from a main video.
    private static readonly NamingOptions _naming = new();

    // The folder names whose videos Jellyfin reads as extras, such as trailers or extras.
    private static readonly HashSet<string> _extraFolders = new(
        _naming.VideoExtraRules.Where(rule => rule.RuleType == ExtraRuleType.DirectoryName).Select(rule => rule.Token),
        StringComparer.OrdinalIgnoreCase);

    // The names Jellyfin reads as artwork and never as a photo, when a file name starts with them.
    private static readonly string[] _artworkNames = ["folder", "thumb", "landscape", "fanart", "backdrop", "poster", "cover", "logo", "default"];

    // Home videos and photos, and in the older layout music videos too, which it keeps as they are; photos share the folders
    // of the home videos.
    private static bool IsUntitledVideo(StubKind kind, bool legacy) => kind is StubKind.HomeVideo or StubKind.Photo || (legacy && kind == StubKind.MusicVideo);

    // Plans the home videos and photos of a server and claims their stubs; the others are left for the sweep.
    private static List<StubWork> VideoWorks(
        StubPlanInput input,
        IndexItemLookup catalog,
        RemoteServerInfo server,
        List<StubEntry> own,
        HashSet<string> claimed,
        HashSet<string> unplaceable)
    {
        var works = new List<StubWork>();
        if (input.Snapshot.Find(server.Id) is not { } serverCatalog)
        {
            return works;
        }

        string? KeyOf(StubEntry stub) => FederatedPaths.RemoteLibraryOf(stub.RelativePath) is { } library
            ? input.PlacementOf(server.Id, library, TypeFolder.Of(stub.Kind))
            : null;
        var existing = new Dictionary<(StubKind, string, string), StubEntry>();
        foreach (var stub in own.Where(stub => stub.Sources.Count > 0).OrderBy(stub => stub.Id, StringComparer.Ordinal))
        {
            if (KeyOf(stub) is { } key)
            {
                existing.TryAdd((stub.Kind, stub.Sources[0].ItemId, key), stub);
            }
        }

        var files = new HashSet<string>(own.Where(stub => stub.Kind == StubKind.HomeVideo).Select(stub => Path.ChangeExtension(stub.RelativePath, null)), StringComparer.OrdinalIgnoreCase);
        var names = new FolderNames(own);
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = serverCatalog.Items
            .Where(item => item.Kind == IndexedItemKind.Video || PhotoPlaceholders.HasPlaceholder(item))
            .OrderBy(item => item.ItemId, StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.Kind == IndexedItemKind.Photo)
            {
                works.AddRange(PhotoWorks(input, catalog, server.Id, item, existing, names, covered, claimed, unplaceable));
                continue;
            }

            // Decision 11: an item whose every version is another JellyMesh's stub has none left, and is not imported.
            var ranked = QualityRanking.Rank(item.Versions.Select(version => new RemoteSource(server.Id.ToString(), item.ItemId, version.MediaSourceId, CatalogItemMapper.QualityOf(version))));
            if (ranked.Count == 0)
            {
                continue;
            }

            const StubKind kind = StubKind.HomeVideo;
            var type = TypeFolder.HomeVideos;
            var placements = LibrariesOf(item).GroupBy(library => input.PlacementOf(server.Id, library, type), StringComparer.Ordinal).ToList();
            if (placements.Count == 0)
            {
                unplaceable.Add(kind + "|" + item.ItemId);
            }

            foreach (var placement in placements)
            {
                var stub = existing.GetValueOrDefault((kind, item.ItemId, placement.Key));
                var typeFolder = FederatedPaths.TypeFolderOf(server.Id, placement.First(), type);
                var sources = ranked.Select(source => new StubSource(server.Id, item.ItemId, source.MediaSourceId) { LibraryId = item.LibraryId }).ToList();
                works.Add(HomeVideoWork(input, item, stub, sources, id => HomeVideoPath(catalog, server.Id, item, typeFolder, files, names, input.IgnoreRules, id)));
                if (stub is not null)
                {
                    claimed.Add(stub.Id);
                }
            }
        }

        return works;
    }

    private static StubWork HomeVideoWork(StubPlanInput input, IndexedItem item, StubEntry? stub, List<StubSource> sources, Func<string, string> newPath)
    {
        var id = stub?.Id ?? NewId();
        var relative = stub?.RelativePath ?? newPath(id);
        var metadata = StubMetadata.From(item, input.IsProtected);
        var nfo = NfoDocument.HomeVideo(metadata);
        List<(ImageType, string)> slots = [(ImageType.Primary, Path.ChangeExtension(relative, null) + "-thumb")];
        return VideoWork(input, item, (stub ?? NewEntry(id, StubKind.HomeVideo, relative)) with { Sources = sources }, stub is null, metadata, nfo, slots);
    }

    private static StubWork VideoWork(StubPlanInput input, IndexedItem item, StubEntry entry, bool isNew, StubMetadata metadata, string nfo, List<(ImageType Type, string Stem)> slots)
    {
        var source = entry.Sources[0];
        entry = entry with { ServerId = source.ServerId, IdentityKey = string.Join('|', entry.Kind, source.ServerId.ToString("N"), item.ItemId) };
        var images = new List<StubImage>();
        foreach (var (type, stem) in slots)
        {
            AddRemote(images, item, source.ServerId, type, stem);
        }

        var url = input.StrmUrl(entry.Id);
        var version = item.Versions.FirstOrDefault(candidate => string.Equals(candidate.MediaSourceId, source.MediaSourceId, StringComparison.Ordinal));
        var technical = string.Join('|', source.ServerId.ToString("N"), item.ItemId, source.MediaSourceId, version?.ETag, item.Etag);
        return new StubWork(entry, isNew, metadata, nfo, Path.ChangeExtension(entry.RelativePath, ".nfo"), url, Hash(nfo, Unsigned(url)), images, ImageTagOf(item, images), technical)
        {
            RemovedImages = Removed(entry.ImageTag, slots, images)
        };
    }

    // The path of a new home video: the remote folders it is in, then its name, unique in its folder and starting no photo.
    private static string HomeVideoPath(IndexItemLookup catalog, Guid serverId, IndexedItem item, string typeFolder, HashSet<string> taken, FolderNames names, IServerIgnoreRules rules, string id)
    {
        var folder = FolderOf(catalog, serverId, item, typeFolder, rules);
        var name = FederatedPaths.Sanitize(item.Name);

        // The server hides a video whose name ends in ".sample", also with the id after it (row 560).
        if (name.EndsWith(".sample", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Replace('.', ' ');
        }

        var stem = Path.Combine(folder, name);
        if (IsTaken(taken, stem) || IsReadAsExtra(stem + ".strm") || rules.Ignores(folder, name + ".strm", false) || names.StartsAPhoto(folder, name))
        {
            stem = string.Create(CultureInfo.InvariantCulture, $"{stem} [{id[..8]}]");
        }

        taken.Add(stem);
        names.AddVideo(folder, Path.GetFileName(stem));
        return stem + ".strm";
    }

    // The folder of a new home video or photo: the remote folders and albums it is in.
    private static string FolderOf(IndexItemLookup catalog, Guid serverId, IndexedItem item, string typeFolder, IServerIgnoreRules rules)
    {
        var folder = typeFolder;
        foreach (var parent in FolderChain(catalog, serverId, item))
        {
            folder = Path.Combine(folder, FolderName(parent, folder, rules));
        }

        return folder;
    }

    // Whether a stem, or its thumbnail, is already one of the stubs or thumbnails of its folder.
    private static bool IsTaken(HashSet<string> taken, string stem)
        => taken.Contains(stem)
            || taken.Contains(stem + ThumbSuffix)
            || (stem.EndsWith(ThumbSuffix, StringComparison.OrdinalIgnoreCase) && taken.Contains(stem[..^ThumbSuffix.Length]));

    // Whether Jellyfin would read a file as an extra of another video, and not as a video of its own.
    private static bool IsReadAsExtra(string path) => ExtraRuleResolver.GetExtraInfo(path, _naming).ExtraType is not null;

    // A remote folder name that ends like a stub file or image, names an extras folder, or is skipped by the server with or
    // without its files, gets its id.
    private static string FolderName(IndexedItem folder, string parent, IServerIgnoreRules rules)
    {
        var name = FederatedPaths.Sanitize(folder.Name);
        return _fileExtensions.Contains(Path.GetExtension(name))
            || _extraFolders.Contains(name)
            || rules.Ignores(parent, name, true)
            || rules.Ignores(Path.Combine(parent, name), ProbeFileName, false)
            ? string.Create(CultureInfo.InvariantCulture, $"{name} [{SafeId(folder.ItemId)}]")
            : name;
    }

    // Up to 8 letters and digits of a remote id, or of its hash when it has none, so that it is safe in a name.
    private static string SafeId(string id)
    {
        var safe = new string(id.Where(char.IsAsciiLetterOrDigit).Take(8).ToArray());
        return safe.Length > 0 ? safe : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..8];
    }

    // The remote folders and albums above an item, outermost first.
    private static List<IndexedItem> FolderChain(IndexItemLookup catalog, Guid serverId, IndexedItem item)
    {
        var names = new List<IndexedItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { item.ItemId };
        var parentId = item.ParentId;
        while (parentId is not null
            && names.Count < MaxFolderDepth
            && seen.Add(parentId)
            && catalog.Find(serverId, parentId) is { Kind: IndexedItemKind.Folder or IndexedItemKind.PhotoAlbum } parent)
        {
            names.Add(parent);
            parentId = parent.ParentId;
        }

        names.Reverse();
        return names;
    }
}
