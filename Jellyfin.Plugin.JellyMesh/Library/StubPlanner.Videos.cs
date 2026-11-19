using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Emby.Naming.Common;
using Emby.Naming.Video;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Plans the music videos and home videos of a server: one stub per remote item and local library, read from the index.
/// </summary>
public static partial class StubPlanner
{
    // Deeper folder chains are cut there, so that a damaged index cannot make paths without end.
    private const int MaxFolderDepth = 32;

    private const string ThumbSuffix = "-thumb";

    // The extensions of the files written next to a home video.
    private static readonly HashSet<string> _fileExtensions = new([".strm", ".nfo", ".jpg", ".png", ".webp", ".gif"], StringComparer.OrdinalIgnoreCase);

    // Jellyfin's own naming rules, which tell an extra from a main video.
    private static readonly NamingOptions _naming = new();

    // The folder names whose videos Jellyfin reads as extras, such as trailers or extras.
    private static readonly HashSet<string> _extraFolders = new(
        _naming.VideoExtraRules.Where(rule => rule.RuleType == ExtraRuleType.DirectoryName).Select(rule => rule.Token),
        StringComparer.OrdinalIgnoreCase);

    private static bool IsVideoKind(StubKind kind) => kind is StubKind.MusicVideo or StubKind.HomeVideo;

    // Plans the music videos and home videos of a server and claims their stubs; the others are left for the sweep.
    private static List<StubWork> VideoWorks(
        StubPlanInput input,
        IndexItemLookup catalog,
        RemoteServerInfo server,
        string label,
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

        var folders = new PathAllocator(own, []);
        var files = new HashSet<string>(own.Where(stub => stub.Kind == StubKind.HomeVideo).Select(stub => Path.ChangeExtension(stub.RelativePath, null)), StringComparer.OrdinalIgnoreCase);
        var items = serverCatalog.Items
            .Where(item => item.Kind is IndexedItemKind.MusicVideo or IndexedItemKind.Video)
            .OrderBy(item => item.ItemId, StringComparer.Ordinal);
        foreach (var item in items)
        {
            // Decision 11: an item whose every version is another JellyMesh's stub has none left, and is not imported.
            var ranked = QualityRanking.Rank(item.Versions.Select(version => new RemoteSource(server.Id.ToString(), item.ItemId, version.MediaSourceId, CatalogItemMapper.QualityOf(version))));
            if (ranked.Count == 0)
            {
                continue;
            }

            var kind = item.Kind == IndexedItemKind.MusicVideo ? StubKind.MusicVideo : StubKind.HomeVideo;
            var type = TypeFolder.Of(kind);
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
                works.Add(kind == StubKind.MusicVideo
                    ? MusicVideoWork(input, item, stub, sources, id => FederatedPaths.MovieStubIn(typeFolder, folders.MovieFolderName(typeFolder, item.Name, item.ProductionYear, id, label), label))
                    : HomeVideoWork(input, item, stub, sources, id => HomeVideoPath(catalog, server.Id, item, typeFolder, files, id)));
                if (stub is not null)
                {
                    claimed.Add(stub.Id);
                }
            }
        }

        return works;
    }

    private static StubWork MusicVideoWork(StubPlanInput input, IndexedItem item, StubEntry? stub, List<StubSource> sources, Func<string, string> newPath)
    {
        var id = stub?.Id ?? NewId();
        var relative = stub?.RelativePath ?? newPath(id);
        var folder = Path.GetDirectoryName(relative)!;
        var metadata = StubMetadata.From(item, input.IsProtected);
        var nfo = NfoDocument.MusicVideo(metadata);
        var slots = _titleImages.Select(image => (image.Type, Path.Combine(folder, image.Stem))).ToList();
        return VideoWork(input, item, (stub ?? NewEntry(id, StubKind.MusicVideo, relative)) with { Sources = sources }, stub is null, metadata, nfo, slots);
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

    // The path of a new home video: the remote folders it is in, then its name, unique in its folder.
    private static string HomeVideoPath(IndexItemLookup catalog, Guid serverId, IndexedItem item, string typeFolder, HashSet<string> taken, string id)
    {
        var folder = Path.Combine([typeFolder, .. FolderChain(catalog, serverId, item)]);
        var stem = Path.Combine(folder, FederatedPaths.Sanitize(item.Name));
        if (IsTaken(taken, stem) || IsReadAsExtra(stem + ".strm"))
        {
            stem = string.Create(CultureInfo.InvariantCulture, $"{stem} [{id[..8]}]");
        }

        taken.Add(stem);
        return stem + ".strm";
    }

    // Whether a stem, or its thumbnail, is already one of the stubs or thumbnails of its folder.
    private static bool IsTaken(HashSet<string> taken, string stem)
        => taken.Contains(stem)
            || taken.Contains(stem + ThumbSuffix)
            || (stem.EndsWith(ThumbSuffix, StringComparison.OrdinalIgnoreCase) && taken.Contains(stem[..^ThumbSuffix.Length]));

    // Whether Jellyfin would read a file as an extra of another video, and not as a video of its own.
    private static bool IsReadAsExtra(string path) => ExtraRuleResolver.GetExtraInfo(path, _naming).ExtraType is not null;

    // A remote folder name that ends like a stub file or image, or names an extras folder, gets its id.
    private static string FolderName(IndexedItem folder)
    {
        var name = FederatedPaths.Sanitize(folder.Name);
        return _fileExtensions.Contains(Path.GetExtension(name)) || _extraFolders.Contains(name)
            ? string.Create(CultureInfo.InvariantCulture, $"{name} [{folder.ItemId[..Math.Min(8, folder.ItemId.Length)]}]")
            : name;
    }

    // The names of the remote folders and albums above an item, outermost first, each a single safe path segment.
    private static List<string> FolderChain(IndexItemLookup catalog, Guid serverId, IndexedItem item)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { item.ItemId };
        var parentId = item.ParentId;
        while (parentId is not null
            && names.Count < MaxFolderDepth
            && seen.Add(parentId)
            && catalog.Find(serverId, parentId) is { Kind: IndexedItemKind.Folder or IndexedItemKind.PhotoAlbum } parent)
        {
            names.Add(FolderName(parent));
            parentId = parent.ParentId;
        }

        names.Reverse();
        return names;
    }
}
