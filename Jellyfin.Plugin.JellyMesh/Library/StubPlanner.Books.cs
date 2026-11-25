using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyMesh.Index;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The book part of the plan: one placeholder per remote ebook, PDF or comic and one <c>.strm</c> per audiobook part, for each
/// local library, with no dedup (927-Q7).
/// </summary>
public static partial class StubPlanner
{
    // Plans the books of one server from its index: a stub stays while its item, file format and local library do.
    private static List<StubWork> BookWorks(StubPlanInput input, Guid serverId, List<StubEntry> stubs, PathAllocator paths, bool legacy, HashSet<string> claimed, HashSet<string> unplaceable)
    {
        if (legacy)
        {
            // Books came after the remote library layout: in an older one the existing are kept as they are.
            claimed.UnionWith(stubs.Select(stub => stub.Id));
            return [];
        }

        if (input.Snapshot.Find(serverId) is not { } catalog)
        {
            return [];
        }

        string? KeyOf(string path) => FederatedPaths.RemoteLibraryOf(path) is { } library ? input.PlacementOf(serverId, library, TypeFolder.Books) : null;
        var byItem = stubs.SelectMany(stub => stub.Sources.Select(source => (source.ItemId, Stub: stub))).ToLookup(pair => pair.ItemId, pair => pair.Stub, StringComparer.Ordinal);
        var works = new List<StubWork>();
        foreach (var book in catalog.Items.Where(item => item.Kind == IndexedItemKind.Book && item.Extension is not null).OrderBy(item => item.ItemId, StringComparer.Ordinal))
        {
            var libraries = LibrariesOf(book).Order(StringComparer.Ordinal).DistinctBy(library => input.PlacementOf(serverId, library, TypeFolder.Books)).ToList();
            if (libraries.Count == 0)
            {
                unplaceable.Add("book|" + serverId.ToString("N") + "|" + book.ItemId);
            }

            foreach (var library in libraries)
            {
                var key = input.PlacementOf(serverId, library, TypeFolder.Books);
                var stub = byItem[book.ItemId].FirstOrDefault(entry => !claimed.Contains(entry.Id)
                    && string.Equals(Path.GetExtension(entry.RelativePath), "." + book.Extension, StringComparison.Ordinal)
                    && KeyOf(entry.RelativePath) == key);
                var folder = FederatedPaths.TypeFolderOf(serverId, library, TypeFolder.Books);
                var work = BookWork(input, book, stub, serverId, library, id =>
                {
                    var name = paths.MovieFolderName(folder, book.Name, book.ProductionYear, id);
                    return Path.Combine(folder, name, name + "." + book.Extension);
                });
                claimed.Add(work.Entry.Id);
                works.Add(work);
            }
        }

        return works;
    }

    // Plans the audiobooks of one server from its index, as tracks: their stubs too are kept in an older layout. Their author
    // folders share the names of the books, because the folder of a book goes whole with it.
    private static List<StubWork> AudioBookWorks(StubPlanInput input, IndexItemLookup catalog, Guid serverId, List<StubEntry> stubs, PathAllocator paths, bool legacy, HashSet<string> claimed)
    {
        var works = legacy ? [] : TrackStubPlanner.Plan(input, catalog, serverId, StubKind.AudioBook, stubs, paths.ArtistFolder);
        claimed.UnionWith(legacy ? stubs.Select(stub => stub.Id) : works.Select(work => work.Entry.Id));
        return works;
    }

    private static StubWork BookWork(StubPlanInput input, IndexedItem book, StubEntry? stub, Guid serverId, string library, Func<string, string> newPath)
    {
        var id = stub?.Id ?? NewId();
        var relative = stub?.RelativePath ?? newPath(id);
        var entry = (stub ?? NewEntry(id, StubKind.Book, relative)) with
        {
            ServerId = serverId,
            IdentityKey = "book|" + book.ItemId,
            Sources = [new StubSource(serverId, book.ItemId, book.ItemId) { LibraryId = library }]
        };

        // A cover named as the file becomes the Primary image of the book, also on a locked item (F48).
        var metadata = StubMetadata.Book(book, input.IsProtected, id);
        var stem = Path.ChangeExtension(relative, null);
        var images = new List<StubImage>();
        AddRemote(images, book, serverId, ImageType.Primary, stem);
        return new StubWork(entry, stub is null, metadata, string.Empty, string.Empty, string.Empty, Hash(JsonSerializer.Serialize(metadata), book.Extension!), images, ImageTagOf(book, images), string.Empty)
        {
            RemovedImages = Removed(entry.ImageTag, [(ImageType.Primary, stem)], images)
        };
    }
}
