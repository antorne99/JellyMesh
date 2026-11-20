using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// What a peer can see: only real copies, only in the libraries shared with it. Every path to an item goes through here,
/// so that the rule lives in one place and the consumer is never trusted with it.
/// </summary>
public sealed class ProviderCatalog
{
    private static readonly ItemFields[] _fields =
    [
        ItemFields.ProviderIds,
        ItemFields.MediaSources,
        ItemFields.DateCreated,
        ItemFields.Etag,
        ItemFields.Overview,
        ItemFields.Genres,
        ItemFields.People,
        ItemFields.Studios,
        ItemFields.Tags,
        ItemFields.Taglines,
        ItemFields.OriginalTitle,
        ItemFields.Chapters,
        ItemFields.RemoteTrailers,
        ItemFields.ParentId,
        ItemFields.Path
    ];

    private readonly ILibraryManager _library;
    private readonly IDtoService _dto;
    private readonly IPluginConfigurationStore _store;
    private readonly ILogger<ProviderCatalog> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderCatalog"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="dto">The DTO service.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="logger">The logger.</param>
    public ProviderCatalog(ILibraryManager library, IDtoService dto, IPluginConfigurationStore store, ILogger<ProviderCatalog> logger)
    {
        _library = library;
        _dto = dto;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Tells whether an item is one this server federates itself. §4.4: such an item never leaves it. The URL covers
    /// video stubs wherever they are; the path, links resolved, covers all under the federated folder in every layout, placeholders included.
    /// The layout of a server folder covers the stubs of every type a moved or cleared federated folder left behind, and its folders.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Whether it is a stub.</returns>
    public bool IsFederated(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item is Video video && PlaybackRoutes.IsPlaybackEndpoint(video.ShortcutPath))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return false;
        }

        var path = FolderPath.Of(item.Path);
        if (HasServerFolderLayout(path.Lexical) || HasServerFolderLayout(path.Canonical) || IsServerFolder(path.Lexical) || IsServerFolder(path.Canonical))
        {
            return true;
        }

        return FederatedRoot() is { } root && path.IsWithin(root);
    }

    /// <summary>
    /// Lists the libraries of this server of a type peers can be given, except those whose paths are all federated.
    /// </summary>
    /// <returns>The libraries.</returns>
    public IReadOnlyList<ProviderLibrary> ReadLibraries()
    {
        var root = FederatedRoot();
        return _library.GetVirtualFolders()
            .Where(folder => ProviderLibraryTypes.NameOf(folder.CollectionType) is not null && Guid.TryParse(folder.ItemId, out _))
            .Where(folder => root is null || !IsOnlyFederated(folder, root))
            .Select(folder => new ProviderLibrary(Guid.Parse(folder.ItemId).ToString("N"), folder.Name ?? string.Empty, ProviderLibraryTypes.NameOf(folder.CollectionType)!))
            .ToList();
    }

    /// <summary>
    /// Finds the libraries an item is in, among those shared with a peer.
    /// </summary>
    /// <param name="grant">The peer.</param>
    /// <param name="item">The item.</param>
    /// <returns>The libraries.</returns>
    public IReadOnlyList<string> SharedLibrariesOf(PeerGrant grant, BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(grant);

        return _library.GetCollectionFolders(item)
            .Select(folder => folder.Id.ToString("N"))
            .Where(grant.SharedLibraryIds.Contains)
            .ToList();
    }

    /// <summary>
    /// Finds an item a peer may reach: a real one that a library shared with it gives.
    /// </summary>
    /// <param name="caller">The peer.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>The item, null if it does not exist as far as the peer is concerned.</returns>
    public BaseItem? FindVisible(PeerCaller caller, Guid itemId) => FindVisible(caller, itemId, new Lazy<Dictionary<string, CollectionTypeOptions?>>(LibraryTypes));

    /// <summary>
    /// Turns changed items into the changes a peer is told of: those it may reach, each once with the libraries that give
    /// it, and an album as its tracks. A change no read of its libraries would return would only make the consumer index it.
    /// </summary>
    /// <param name="caller">The peer.</param>
    /// <param name="itemIds">The changed items.</param>
    /// <returns>The changes.</returns>
    public IReadOnlyList<ProviderChange> ReadChanges(PeerCaller caller, IEnumerable<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(itemIds);

        var types = new Lazy<Dictionary<string, CollectionTypeOptions?>>(LibraryTypes);
        var announced = new HashSet<Guid>();
        var changes = new List<ProviderChange>();
        foreach (var itemId in itemIds)
        {
            if (FindVisible(caller, itemId, types) is not { } item)
            {
                continue;
            }

            foreach (var entry in item is MusicAlbum ? TracksOf(caller, item) : [item])
            {
                if (announced.Add(entry.Id) && !IsFederated(entry) && LibrariesGiving(caller.Grant, entry, types) is { Count: > 0 } libraries)
                {
                    changes.Add(new ProviderChange(entry.Id.ToString("N"), entry.GetBaseItemKind().ToString(), libraries));
                }
            }
        }

        return changes;
    }

    /// <summary>
    /// Reads one page of a shared library.
    /// </summary>
    /// <param name="caller">The peer.</param>
    /// <param name="libraryId">The library, which must be shared with the peer.</param>
    /// <param name="type">The item type.</param>
    /// <param name="startIndex">The index of the first item.</param>
    /// <param name="limit">The most items to return.</param>
    /// <returns>The page.</returns>
    public ProviderItemsResponse ReadLibrary(PeerCaller caller, Guid libraryId, RemoteItemType type, int startIndex, int limit)
    {
        ArgumentNullException.ThrowIfNull(caller);

        // The exclusion is in the query so that the total stays true for the paging of the consumer.
        if (!ProviderLibraryTypes.Reads(type) || !Gives(libraryId, ProviderLibraryTypes.KindOf(type)) || ScopeOf(libraryId) is not { } scope)
        {
            return Empty();
        }

        var page = _library.GetItemsResult(PageQuery(caller, libraryId, scope, ProviderLibraryTypes.KindOf(type), startIndex, limit));

        // Should a stub get past the query, the page comes up short: the read fails and nothing leaks.
        return ToResponse(caller, page.Items.Where(item => !IsFederated(item)).ToList(), page.TotalRecordCount);
    }

    /// <summary>
    /// Reads one page of the films of a shared library that are in a collection the peer can see, each with that
    /// collection. The films are those <see cref="ReadLibrary"/> reads, so a stub never comes up (decision 198).
    /// </summary>
    /// <param name="caller">The peer.</param>
    /// <param name="libraryId">The library, which must be shared with the peer.</param>
    /// <param name="startIndex">The index of the first film.</param>
    /// <param name="limit">The most films to return.</param>
    /// <returns>The page.</returns>
    public ProviderCollectionsResponse ReadCollections(PeerCaller caller, Guid libraryId, int startIndex, int limit)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var collections = CollectionsOfFilms(caller);
        if (collections.Count == 0 || !Gives(libraryId, BaseItemKind.Movie) || ScopeOf(libraryId) is not { } scope)
        {
            return new ProviderCollectionsResponse([], 0);
        }

        var query = PageQuery(caller, libraryId, scope, BaseItemKind.Movie, startIndex, limit);
        query.ItemIds = [.. collections.Keys];
        var page = _library.GetItemsResult(query);
        var entries = page.Items
            .Where(item => !IsFederated(item) && collections.ContainsKey(item.Id))
            .Select(item => new ProviderCollectionEntry(item.Id.ToString("N"), collections[item.Id].Name, collections[item.Id].TmdbId))
            .ToList();
        return new ProviderCollectionsResponse(entries, page.TotalRecordCount);
    }

    /// <summary>
    /// Reads items by identifier, leaving out those the peer may not reach.
    /// </summary>
    /// <param name="caller">The peer.</param>
    /// <param name="itemIds">The identifiers to look up.</param>
    /// <param name="type">The item type.</param>
    /// <returns>The items found.</returns>
    public ProviderItemsResponse ReadItems(PeerCaller caller, IReadOnlyList<Guid> itemIds, RemoteItemType type)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(itemIds);

        if (!ProviderLibraryTypes.Reads(type))
        {
            return Empty();
        }

        var kind = ProviderLibraryTypes.KindOf(type);
        var types = new Lazy<Dictionary<string, CollectionTypeOptions?>>(LibraryTypes);
        var items = itemIds
            .Select(id => FindVisible(caller, id, types))
            .OfType<BaseItem>()
            .Where(item => item.GetBaseItemKind() == kind)
            .ToList();
        return ToResponse(caller, items, items.Count);
    }

    // The tracks of an album the user of the peer can see, at any depth.
    private IReadOnlyList<BaseItem> TracksOf(PeerCaller caller, BaseItem album) => _library.GetItemList(new InternalItemsQuery(caller.User)
    {
        ParentId = album.Id,
        Recursive = true,
        IncludeItemTypes = [BaseItemKind.Audio],
        IsVirtualItem = false
    });

    // The type of each library of this server, by its identifier.
    private Dictionary<string, CollectionTypeOptions?> LibraryTypes() => _library.GetVirtualFolders()
        .Where(folder => Guid.TryParse(folder.ItemId, out _))
        .GroupBy(folder => Guid.Parse(folder.ItemId).ToString("N"), StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().CollectionType, StringComparer.Ordinal);

    // Whether a library of this server gives items of a kind; a library no longer on it gives none.
    private bool Gives(Guid libraryId, BaseItemKind kind)
    {
        if (LibraryTypes().TryGetValue(libraryId.ToString("N"), out var type))
        {
            return ProviderLibraryTypes.Gives(type, kind);
        }

        _logger.LogDebug("Library {LibraryId} is no longer on this server, so a page of it reads as empty", libraryId);
        return false;
    }

    // The libraries shared with a peer whose type gives it an item (ProviderLibraryTypes.Gives).
    private IReadOnlyList<string> LibrariesGiving(PeerGrant grant, BaseItem item, Lazy<Dictionary<string, CollectionTypeOptions?>> types)
    {
        ArgumentNullException.ThrowIfNull(item);

        var shared = SharedLibrariesOf(grant, item);
        if (shared.Count == 0)
        {
            return shared;
        }

        var kind = item.GetBaseItemKind();
        return shared.Where(id => types.Value.TryGetValue(id, out var type) && ProviderLibraryTypes.Gives(type, kind)).ToList();
    }

    private BaseItem? FindVisible(PeerCaller caller, Guid itemId, Lazy<Dictionary<string, CollectionTypeOptions?>> types)
    {
        ArgumentNullException.ThrowIfNull(caller);

        return _library.GetItemById<BaseItem>(itemId, caller.User) is { } item
            && !IsFederated(item)
            && LibrariesGiving(caller.Grant, item, types).Count > 0
                ? item
                : null;
    }

    // A type no library gives reads as empty rather than refused, so that the read of the rest of the library goes on.
    private static ProviderItemsResponse Empty() => new([], 0, new Dictionary<string, RemoteQuality>(StringComparer.Ordinal));

    // A page of a library as the peer sees it, under the scope that leaves the federated folder out.
    private static InternalItemsQuery PageQuery(PeerCaller caller, Guid libraryId, LibraryScope scope, BaseItemKind kind, int startIndex, int limit) => new(caller.User)
    {
        ParentId = libraryId,
        Recursive = true,
        IncludeItemTypes = [kind],
        IsVirtualItem = false,
        AncestorIds = scope.AncestorIds,
        ExcludeItemIds = scope.ExcludeItemIds,
        OrderBy = [(ItemSortBy.DateCreated, SortOrder.Ascending), (ItemSortBy.SortName, SortOrder.Ascending)],
        StartIndex = startIndex,
        Limit = limit,
        EnableTotalRecordCount = true
    };

    // The consumer needs only the format of a book for its placeholder (D47), so no name or folder of this server leaves.
    private static string? ExtensionOf(string? path)
    {
        var extension = Path.GetExtension(path);
        return extension is { Length: >= 2 and <= 6 } && extension[1..].All(char.IsAsciiLetterOrDigit) ? extension.ToLowerInvariant() : null;
    }

    private static RemoteQuality? QualityOf(MediaSourceInfo source)
    {
        var videos = source.MediaStreams?.Where(stream => stream.Type == MediaStreamType.Video).ToList() ?? [];
        var video = videos.Find(stream => stream.IsDefault) ?? videos.FirstOrDefault();
        return video is null
            ? null
            : new RemoteQuality(video.Height, video.Width, video.BitRate, video.Codec, video.VideoRange.ToString(), video.VideoRangeType.ToString());
    }

    private ProviderItemsResponse ToResponse(PeerCaller caller, IReadOnlyList<BaseItem> items, int total)
    {
        var options = new DtoOptions(false) { Fields = _fields, EnableImages = true, EnableUserData = false };
        var dtos = _dto.GetBaseItemDtos(items, options, caller.User);

        var quality = new Dictionary<string, RemoteQuality>(StringComparer.Ordinal);
        foreach (var source in dtos.SelectMany(dto => dto.MediaSources ?? []))
        {
            if (source.Id is { } id && QualityOf(source) is { } value)
            {
                quality[id] = value;
            }

            // The consumer has no use for where files are on this server.
            source.Path = null;
            foreach (var stream in source.MediaStreams ?? [])
            {
                stream.Path = null;
            }
        }

        // Watch state stays on this server (decision 41), even if a DTO came back with it.
        foreach (var dto in dtos)
        {
            dto.UserData = null;
            dto.Path = dto.Type == BaseItemKind.Book ? ExtensionOf(dto.Path) : null;
        }

        // The file of a chapter image is a path of this server too.
        foreach (var chapter in dtos.SelectMany(dto => dto.Chapters ?? []))
        {
            chapter.ImagePath = null;
        }

        return new ProviderItemsResponse(dtos, total, quality);
    }

    // The collections the peer can see, by member. A film in several keeps the first by name, as the stock read does.
    // A link without an item ID, kept only by the obsolete path, is not followed.
    private Dictionary<Guid, IndexedBoxSet> CollectionsOfFilms(PeerCaller caller)
    {
        var boxSets = _library.GetItemList(new InternalItemsQuery(caller.User) { IncludeItemTypes = [BaseItemKind.BoxSet], Recursive = true })
            .OfType<BoxSet>()
            .Select(boxSet => (boxSet, Collection: IndexedBoxSet.From(boxSet.Name, boxSet.GetProviderId(MetadataProvider.Tmdb))))
            .Where(entry => entry.Collection is not null)
            .OrderBy(entry => entry.Collection!.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.boxSet.Id);

        var members = new Dictionary<Guid, IndexedBoxSet>();
        foreach (var (boxSet, collection) in boxSets)
        {
            foreach (var child in boxSet.LinkedChildren ?? [])
            {
                if (child?.ItemId is { } id && id != Guid.Empty)
                {
                    members.TryAdd(id, collection!);
                }
            }
        }

        return members;
    }

    // Looks for {server guid}/{library id}/{type folder} anywhere in the path, as the plugin writes the folder of a server.
    private static bool HasServerFolderLayout(string path)
    {
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 2 < parts.Length; i++)
        {
            if (Guid.TryParseExact(parts[i], "D", out _)
                && FederatedPaths.IsLibraryId(parts[i + 1])
                && TypeFolder.Written.Any(type => string.Equals(type.Name, parts[i + 2], PathIdentity.Comparison)))
            {
                return true;
            }
        }

        return false;
    }

    // The folder of a server's library, {server guid}/{library id}, or the folder of a server holding such folders.
    private static bool IsServerFolder(string path)
    {
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && Guid.TryParseExact(parts[^2], "D", out _) && FederatedPaths.IsLibraryId(parts[^1]))
        {
            return true;
        }

        return parts.Length >= 1 && Guid.TryParseExact(parts[^1], "D", out _) && HoldsServerFolders(path);
    }

    // Whether a folder holds the folder of a library or of a type, as the folder of a server does.
    private static bool HoldsServerFolders(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path)
                .Select(Path.GetFileName)
                .Any(name => FederatedPaths.IsLibraryId(name) || TypeFolder.Written.Any(type => string.Equals(type.Name, name, PathIdentity.Comparison)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsOnlyFederated(VirtualFolderInfo library, FolderPath root)
    {
        var locations = Locations(library);
        return locations.Count > 0 && locations.All(location => location.IsWithin(root));
    }

    // The paths of a library, normalized as the federated folder is.
    private static List<FolderPath> Locations(VirtualFolderInfo library) =>
        (library.Locations ?? []).Where(location => !string.IsNullOrWhiteSpace(location)).Select(FolderPath.Of).ToList();

    private FolderPath? FederatedRoot()
    {
        var root = _store.Read(config => config.FederatedFolderPath);
        return string.IsNullOrWhiteSpace(root) ? null : FolderPath.Of(root);
    }

    /// <summary>
    /// Narrows a page of a library to what is outside the federated folder, at any depth. Null when there is nothing to read.
    /// </summary>
    private LibraryScope? ScopeOf(Guid libraryId)
    {
        if (FederatedRoot() is not { } root)
        {
            return LibraryScope.Whole;
        }

        var library = _library.GetVirtualFolders().Find(folder => Guid.TryParse(folder.ItemId, out var id) && id == libraryId);
        if (library is null)
        {
            _logger.LogDebug("Library {LibraryId} is no longer on this server, so a page of it reads as empty", libraryId);
            return null;
        }

        var locations = Locations(library);
        var federated = locations.Where(location => location.IsWithin(root)).ToList();
        var above = locations.Where(location => root.IsWithin(location) && !location.IsWithin(root)).ToList();
        if (above.Count > 0)
        {
            // Jellyfin names the federated folder after the path of the library, which may reach it by a link.
            var folders = above.Select(location => location.Below(root))
                .OfType<string>()
                .Prepend(root.Indexed)
                .Concat(federated.Select(location => location.Indexed))
                .Distinct(StringComparer.Ordinal)
                .Select(path => _library.FindByPath(path, true))
                .OfType<BaseItem>()
                .Select(folder => folder.Id)
                .Distinct()
                .ToArray();
            // The folders themselves go too: a series library indexes a folder right under its path as a series.
            return folders.Length == 0
                ? LibraryScope.Whole
                : new LibraryScope([], [.. folders, .. _library.GetItemIds(new InternalItemsQuery { AncestorIds = folders, Recursive = true })]);
        }

        if (federated.Count == 0)
        {
            return LibraryScope.Whole;
        }

        var paths = locations.Except(federated).Select(location => location.Indexed).ToList();
        var real = paths
            .Select(path => _library.FindByPath(path, true))
            .OfType<BaseItem>()
            .Select(folder => folder.Id)
            .ToArray();
        if (real.Length == 0 && paths.Count > 0)
        {
            _logger.LogDebug("The real paths of library {LibraryId} are not indexed yet, so a page of it reads as empty", libraryId);
        }

        return real.Length == 0 ? null : new LibraryScope(real, []);
    }

    // A path as Jellyfin indexes it, which is what it is looked up by, and as it is on disk. It is within a folder if
    // either form is, so that a link inside the federated folder pointing out of it does not let its content out.
    private sealed record FolderPath(string Indexed, string Lexical, string Canonical)
    {
        public static FolderPath Of(string path)
        {
            var indexed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return new(indexed, indexed.Normalize(NormalizationForm.FormC), PathIdentity.Canonical(path));
        }

        public bool IsWithin(FolderPath folder) =>
            PathIdentity.IsWithin(Lexical, folder.Lexical, PathIdentity.Comparison) || PathIdentity.IsWithin(Canonical, folder.Canonical, PathIdentity.Comparison);

        // A folder within this one on disk, named from the indexed path of this one. Null if it is not within on disk.
        public string? Below(FolderPath folder) =>
            PathIdentity.IsWithin(folder.Canonical, Canonical, PathIdentity.Comparison)
                ? Path.Join(Indexed, folder.Canonical[Canonical.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : null;
    }

    // The query terms of a page: the folders to read under and the items to leave out.
    private sealed record LibraryScope(Guid[] AncestorIds, Guid[] ExcludeItemIds)
    {
        public static LibraryScope Whole { get; } = new([], []);
    }
}
