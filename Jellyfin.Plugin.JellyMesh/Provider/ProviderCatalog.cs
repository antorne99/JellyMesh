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
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// What a peer can see: only real copies, only in the libraries shared with it. Every path to an item goes through here,
/// so that the rule lives in one place and the consumer is never trusted with it.
/// </summary>
public sealed class ProviderCatalog
{
    // How many parents of an item without a path are looked at for one.
    private const int MaxParents = 4;

    // The page size of a search for hidden items, which no peer sees.
    private const int SearchPageSize = 500;

    /// <summary>The most item ids one query of the library is given, asked for and left out together. Each is one SQL variable.</summary>
    internal const int MaxIdsPerQuery = 10_000;

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
        ItemFields.Path,
        ItemFields.Width,
        ItemFields.Height
    ];

    private readonly ILibraryManager _library;
    private readonly IDtoService _dto;
    private readonly IPluginConfigurationStore _store;
    private readonly ILogger<ProviderCatalog> _logger;
    private readonly HiddenItems _hidden;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderCatalog"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="dto">The DTO service.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="logger">The logger.</param>
    public ProviderCatalog(ILibraryManager library, IDtoService dto, IPluginConfigurationStore store, ILogger<ProviderCatalog> logger)
        : this(library, dto, store, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderCatalog"/> class with a clock.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="dto">The DTO service.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="clock">The clock.</param>
    internal ProviderCatalog(ILibraryManager library, IDtoService dto, IPluginConfigurationStore store, ILogger<ProviderCatalog> logger, TimeProvider clock)
    {
        _library = library;
        _dto = dto;
        _store = store;
        _logger = logger;
        _hidden = new HiddenItems(clock);
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

        if (NearestPath(item) is not { } nearest)
        {
            return false;
        }

        var path = FolderPath.Of(nearest);
        if (HasServerFolderLayout(path) || IsServerFolder(path.Lexical) || IsServerFolder(path.Canonical))
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
    /// Tells whether an item, as a peer reaches it, is a .strm file of this server. Its text is a URL that may carry
    /// credentials, so neither its file nor its path goes to a peer (rows 561 and 565).
    /// </summary>
    /// <param name="caller">The peer.</param>
    /// <param name="itemId">The item, or the version as its media source names it.</param>
    /// <returns>Whether it is a .strm file.</returns>
    public bool IsStrmFile(PeerCaller caller, Guid itemId)
    {
        ArgumentNullException.ThrowIfNull(caller);

        return !itemId.Equals(Guid.Empty) && IsStrm(_library.GetItemById<BaseItem>(itemId, caller.User)?.Path);
    }

    /// <summary>
    /// Tells whether a path is a .strm file, whatever the case of its extension.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>Whether it is one.</returns>
    public static bool IsStrm(string? path)
        => string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Clears the paths of a version and of its streams, keeping only the extension of a file of this server for the
    /// consumer's container, and every field that can hold the URL a version reads. For PlaybackInfo and the catalog (rows 566-569).
    /// </summary>
    /// <param name="caller">The peer.</param>
    /// <param name="source">The version, cleared in place.</param>
    public void ClearUrlFields(PeerCaller caller, MediaSourceInfo source)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(source);

        var isUrl = IsUrlSource(caller, source);
        source.Path = isUrl ? null : ExtensionOf(source.Path);
        source.EncoderPath = null;
        foreach (var stream in source.MediaStreams ?? [])
        {
            stream.Path = null;
        }

        if (!isUrl)
        {
            return;
        }

        source.RequiredHttpHeaders = [];
        foreach (var stream in source.MediaStreams ?? [])
        {
            stream.DeliveryUrl = null;
        }

        foreach (var attachment in source.MediaAttachments ?? [])
        {
            attachment.DeliveryUrl = null;
        }
    }

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
    /// Tells whether a peer may see a version of an item it may reach: the item itself, or a real copy shared with it.
    /// </summary>
    /// <param name="caller">The peer.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="versionId">The version, as its media source names it.</param>
    /// <returns>Whether it may, false for a version that cannot be found.</returns>
    public bool MaySeeVersion(PeerCaller caller, Guid itemId, string? versionId)
    {
        ArgumentNullException.ThrowIfNull(caller);

        return Guid.TryParse(versionId, out var id)
            && !id.Equals(Guid.Empty)
            && (id.Equals(itemId) || FindVisible(caller, id) is not null || IsFileOfVisible(caller, id));
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

        var kind = ProviderLibraryTypes.KindOf(type);
        if (startIndex == 0)
        {
            ForgetHidden(libraryId, kind);
        }

        var page = ReadPage(caller, libraryId, scope, kind, startIndex, limit);
        return ToResponse(caller, WithoutHidden(caller, libraryId, scope, kind, page.Items), page.TotalRecordCount);
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

        // Past one query's worth of ids, the films left out are taken off the films asked for instead.
        var excluded = Excluded(scope, _hidden.Of(libraryId, BaseItemKind.Movie));
        var fits = excluded.Length + collections.Count <= MaxIdsPerQuery;
        var query = PageQuery(caller, libraryId, scope, BaseItemKind.Movie, fits ? excluded : [], startIndex, limit);
        query.ItemIds = fits ? [.. collections.Keys] : [.. collections.Keys.Except(excluded)];
        if (query.ItemIds.Length == 0)
        {
            return new ProviderCollectionsResponse([], 0);
        }

        var page = _library.GetItemsResult(query);
        var entries = WithoutHidden(caller, libraryId, scope, BaseItemKind.Movie, page.Items)
            .Where(item => collections.ContainsKey(item.Id))
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

    // The items a page of a library leaves out: those the scope leaves out, then those known to be hidden.
    private static Guid[] Excluded(LibraryScope scope, IReadOnlySet<Guid> hidden) => [.. scope.ExcludeItemIds.Concat(hidden).Distinct()];

    // A page of a library as the peer sees it, under the scope that leaves the federated folder out and without the
    // given items.
    private static InternalItemsQuery PageQuery(PeerCaller caller, Guid libraryId, LibraryScope scope, BaseItemKind kind, IReadOnlyCollection<Guid> excluded, int startIndex, int limit) => new(caller.User)
    {
        ParentId = libraryId,
        Recursive = true,
        IncludeItemTypes = [kind],
        IsVirtualItem = false,
        AncestorIds = scope.AncestorIds,
        ExcludeItemIds = [.. excluded],
        OrderBy = [(ItemSortBy.DateCreated, SortOrder.Ascending), (ItemSortBy.SortName, SortOrder.Ascending)],
        StartIndex = startIndex,
        Limit = limit,
        EnableTotalRecordCount = true
    };

    // Reads a page of a library without the items it leaves out. Past one query's worth of ids, the query leaves out the
    // first ones and the rest are dropped while walking its ordered pages, and taken off its total by counting them in blocks.
    private QueryResult<BaseItem> ReadPage(PeerCaller caller, Guid libraryId, LibraryScope scope, BaseItemKind kind, int startIndex, int limit)
    {
        var excluded = Excluded(scope, _hidden.Of(libraryId, kind));
        if (excluded.Length <= MaxIdsPerQuery)
        {
            return _library.GetItemsResult(PageQuery(caller, libraryId, scope, kind, excluded, startIndex, limit));
        }

        var inQuery = excluded[..MaxIdsPerQuery];
        var dropped = excluded[MaxIdsPerQuery..].ToHashSet();
        var items = new List<BaseItem>();
        var seen = 0;
        var start = 0;
        QueryResult<BaseItem> walked;
        do
        {
            walked = _library.GetItemsResult(PageQuery(caller, libraryId, scope, kind, inQuery, start, SearchPageSize));
            foreach (var item in walked.Items.Where(item => !dropped.Contains(item.Id)))
            {
                if (seen++ >= startIndex && items.Count < limit)
                {
                    items.Add(item);
                }
            }

            start += SearchPageSize;
        }
        while (items.Count < limit && walked.Items.Count > 0 && start < walked.TotalRecordCount);

        var matched = dropped.Chunk(MaxIdsPerQuery).Sum(block => CountOf(caller, libraryId, scope, kind, block));
        return new QueryResult<BaseItem>(startIndex, Math.Max(walked.TotalRecordCount - matched, 0), items);
    }

    // How many of the given items a page of a library would hold, were they not left out.
    private int CountOf(PeerCaller caller, Guid libraryId, LibraryScope scope, BaseItemKind kind, Guid[] block)
    {
        var query = PageQuery(caller, libraryId, scope, kind, [], 0, 1);
        query.ItemIds = block;
        return _library.GetItemsResult(query).TotalRecordCount;
    }

    // Drops the stubs that got past the query of a page, which comes up short: that read fails and nothing leaks. The
    // next one leaves out every hidden item of the library.
    private List<BaseItem> WithoutHidden(PeerCaller caller, Guid libraryId, LibraryScope scope, BaseItemKind kind, IReadOnlyList<BaseItem> items)
    {
        var hidden = items.Where(IsFederated).Select(item => item.Id).ToHashSet();
        if (hidden.Count > 0)
        {
            LearnHidden(caller, libraryId, scope, kind, hidden);
        }

        return items.Where(item => !hidden.Contains(item.Id)).ToList();
    }

    // Remembers the hidden items of a page and, once one not known yet came up, those of the whole library. A request that
    // finds that search running leaves it to it.
    private void LearnHidden(PeerCaller caller, Guid libraryId, LibraryScope scope, BaseItemKind kind, IReadOnlyCollection<Guid> hidden)
    {
        var known = _hidden.Of(libraryId, kind);
        if (known.IsSupersetOf(hidden))
        {
            return;
        }

        _hidden.KeepOnly(LibraryTypes().Keys.Select(Guid.Parse).ToHashSet());
        _hidden.Add(libraryId, kind, hidden);
        if (!_hidden.TryBeginSearch(libraryId, kind))
        {
            return;
        }

        try
        {
            SearchHidden(caller, libraryId, scope, kind, known);
        }
        finally
        {
            _hidden.EndSearch(libraryId, kind);
        }
    }

    // Reads a library for its hidden items, up to the most kept. The exclusion holds still, since pages are offsets.
    private void SearchHidden(PeerCaller caller, Guid libraryId, LibraryScope scope, BaseItemKind kind, IReadOnlySet<Guid> known)
    {
        var excluded = Excluded(scope, known);
        var inQuery = excluded.Length <= MaxIdsPerQuery ? excluded : excluded[..MaxIdsPerQuery];
        var dropped = excluded.Skip(inQuery.Length).ToHashSet();
        var start = 0;
        QueryResult<BaseItem> page;
        do
        {
            page = _library.GetItemsResult(PageQuery(caller, libraryId, scope, kind, inQuery, start, SearchPageSize));
            _hidden.Add(libraryId, kind, page.Items.Where(item => !dropped.Contains(item.Id) && IsFederated(item)).Select(item => item.Id).ToList());
            start += SearchPageSize;
        }
        while (page.Items.Count > 0 && start < page.TotalRecordCount && _hidden.Of(libraryId, kind).Count < HiddenItems.MaxPerType);
    }

    // Forgets the hidden items of a library that are gone or no longer hidden.
    private void ForgetHidden(Guid libraryId, BaseItemKind kind)
    {
        if (_hidden.TryBeginCheck(libraryId, kind))
        {
            _hidden.Remove(libraryId, kind, _hidden.Of(libraryId, kind).Where(id => _library.GetItemById(id) is not { } item || !IsFederated(item)).ToList());
        }
    }

    // The consumer needs only the format of a book or a photo for its placeholder (D47), or of a file for its container
    // (row 569), so no name or folder of this server leaves.
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

    // Tells whether a source reads an admin's .strm, or anything but a file of this server: its path is then a URL.
    // A source whose id names no item cannot be checked for a .strm, so it counts as one.
    private bool IsUrlSource(PeerCaller caller, MediaSourceInfo source)
    {
        return source.Protocol != MediaProtocol.File
            || source.IsRemote
            || !Guid.TryParse(source.Id, out var id)
            || id.Equals(Guid.Empty)
            || _library.GetItemById<BaseItem>(id, caller.User) is not { } item
            || IsStrm(item.Path);
    }

    // A file next to the main one of an item, with no library of its own, is seen with that item.
    private bool IsFileOfVisible(PeerCaller caller, Guid itemId)
    {
        return _library.GetItemById<BaseItem>(itemId, caller.User) is Video { OwnerId: var ownerId } file
            && !ownerId.Equals(Guid.Empty)
            && !IsFederated(file)
            && _library.GetCollectionFolders(file).Count == 0
            && FindVisible(caller, ownerId) is not null;
    }

    private ProviderItemsResponse ToResponse(PeerCaller caller, IReadOnlyList<BaseItem> items, int total)
    {
        var options = new DtoOptions(false) { Fields = _fields, EnableImages = true, EnableUserData = false };
        var dtos = _dto.GetBaseItemDtos(items, options, caller.User);

        foreach (var dto in dtos.Where(dto => dto.MediaSources is not null))
        {
            dto.MediaSources = [.. dto.MediaSources.Where(source => MaySeeVersion(caller, dto.Id, source.Id))];
        }

        var quality = new Dictionary<string, RemoteQuality>(StringComparer.Ordinal);
        foreach (var source in dtos.SelectMany(dto => dto.MediaSources ?? []))
        {
            if (source.Id is { } id && QualityOf(source) is { } value)
            {
                quality[id] = value;
            }

            // The consumer has no use for where files are on this server, nor for the URL a source reads.
            ClearUrlFields(caller, source);
        }

        // Watch state stays on this server (decision 41), even if a DTO came back with it. No place a photo was taken
        // leaves either (row 560).
        foreach (var dto in dtos)
        {
            dto.UserData = null;
            dto.Path = dto.Type is BaseItemKind.Book or BaseItemKind.Photo ? ExtensionOf(dto.Path) : null;
            dto.Latitude = null;
            dto.Longitude = null;
            dto.Altitude = null;
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

    // The path of an item, or of its nearest parent that has one, such as the series of a season without a folder.
    private string? NearestPath(BaseItem item)
    {
        BaseItem? current = item;
        for (var depth = 0; current is not null && depth <= MaxParents; depth++)
        {
            if (!string.IsNullOrWhiteSpace(current.Path))
            {
                return current.Path;
            }

            current = current.ParentId.Equals(Guid.Empty) ? null : _library.GetItemById(current.ParentId);
        }

        return null;
    }

    private static bool HasServerFolderLayout(FolderPath path) => HasServerFolderLayout(path.Lexical) || HasServerFolderLayout(path.Canonical);

    // Looks for the folder of a server anywhere in the path: {server guid}/{library id}/{type folder}, or
    // {server guid}/{type folder} of the 0.5 layout, which held only films and series (decision 513).
    private static bool HasServerFolderLayout(string path)
    {
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            if (Guid.TryParseExact(parts[i], "D", out _)
                && (IsAny(TypeFolder.Older, parts[i + 1])
                    || (i + 2 < parts.Length && FederatedPaths.IsLibraryId(parts[i + 1]) && IsAny(TypeFolder.Written, parts[i + 2]))))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAny(IReadOnlyList<TypeFolder> types, string? name) =>
        types.Any(type => string.Equals(type.Name, name, PathIdentity.Comparison));

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
                .Any(name => FederatedPaths.IsLibraryId(name) || IsAny(TypeFolder.Written, name));
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
    /// Narrows a page of a library to what is outside the federated folder, at any depth, and outside the paths of the library
    /// that are a server folder left by a moved one. Null when there is nothing to read.
    /// </summary>
    private LibraryScope? ScopeOf(Guid libraryId)
    {
        var library = _library.GetVirtualFolders().Find(folder => Guid.TryParse(folder.ItemId, out var id) && id == libraryId);
        if (library is null)
        {
            _logger.LogDebug("Library {LibraryId} is no longer on this server, so a page of it reads as empty", libraryId);
            return null;
        }

        // Every item under a path named with the layout of a server folder is hidden, whatever the root is now. A path that
        // only links to one is not left out, since what it holds may link out of it.
        var root = FederatedRoot();
        var locations = Locations(library);
        var federated = root is null ? [] : locations.Where(location => location.IsWithin(root)).ToList();
        var rest = locations.Except(federated).ToList();
        var old = rest.Where(location => HasServerFolderLayout(location.Lexical)).ToList();
        var real = rest.Except(old).ToList();
        var above = root is null ? [] : real.Where(root.IsWithin).ToList();
        if (above.Count > 0 || real.Exists(location => old.Exists(folder => folder.IsNamedWithin(location))))
        {
            // Jellyfin names the federated folder after the path of the library, which may reach it by a link.
            var folders = above.Select(location => location.Below(root!))
                .Prepend(above.Count > 0 ? root!.Indexed : null)
                .OfType<string>()
                .Concat(federated.Concat(old).Select(folder => folder.Indexed))
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

        if (federated.Count == 0 && old.Count == 0)
        {
            return LibraryScope.Whole;
        }

        // A read that fails keeps what the peer already has, so an old folder is left out only if no real path is lost with it.
        var indexed = IndexedIds(real);
        if (indexed.Length < real.Count && old.Count > 0)
        {
            if (federated.Count == 0)
            {
                return LibraryScope.Whole;
            }

            indexed = IndexedIds(rest);
        }

        if (indexed.Length == 0 && real.Count > 0)
        {
            _logger.LogDebug("The real paths of library {LibraryId} are not indexed yet, so a page of it reads as empty", libraryId);
        }

        return indexed.Length == 0 ? null : new LibraryScope(indexed, []);
    }

    private Guid[] IndexedIds(IEnumerable<FolderPath> paths) => paths
        .Select(path => _library.FindByPath(path.Indexed, true))
        .OfType<BaseItem>()
        .Select(folder => folder.Id)
        .ToArray();

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

        // Whether it is within a folder by its name alone, links not resolved.
        public bool IsNamedWithin(FolderPath folder) => PathIdentity.IsWithin(Lexical, folder.Lexical, PathIdentity.Comparison);

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
