using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyMesh.Index;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The book part of the plan: one placeholder per remote ebook, PDF or comic and local library, with no dedup (927-Q7).
/// </summary>
public static partial class StubPlanner
{
    // Plans the books of one server from its index: a stub stays while its item, file format and local library do.
    private static List<StubWork> BookWorks(StubPlanInput input, Guid serverId, List<StubEntry> own, bool legacy, HashSet<string> claimed, HashSet<string> unplaceable)
    {
        var stubs = own.Where(stub => stub.Kind == StubKind.Book).ToList();
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
        var paths = new PathAllocator(stubs, [], input.IgnoreRules);
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
                var stub = stubs.Find(entry => !claimed.Contains(entry.Id)
                    && entry.Sources.Any(source => source.ItemId == book.ItemId)
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
        var metadata = StubMetadata.From(book, input.IsProtected);
        var stem = Path.ChangeExtension(relative, null);
        var images = new List<StubImage>();
        AddRemote(images, book, serverId, ImageType.Primary, stem);
        return new StubWork(entry, stub is null, metadata, string.Empty, string.Empty, string.Empty, Hash(JsonSerializer.Serialize(metadata), book.Extension!), images, ImageTagOf(book, images), string.Empty)
        {
            RemovedImages = Removed(entry.ImageTag, [(ImageType.Primary, stem)], images)
        };
    }
}
