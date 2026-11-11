using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
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
        ItemFields.Chapters
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
    /// films and episodes wherever they are, and the path, with its links resolved, covers the series and seasons, which have no URL.
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

        var root = FederatedRoot();
        return root is not null && !string.IsNullOrWhiteSpace(item.Path) && FolderPath.Of(item.Path).IsWithin(root);
    }

    /// <summary>
    /// Lists the film and series libraries of this server, except those whose paths are all federated.
    /// </summary>
    /// <returns>The libraries.</returns>
    public IReadOnlyList<ProviderLibrary> ReadLibraries()
    {
        var root = FederatedRoot();
        return _library.GetVirtualFolders()
            .Where(folder => folder.CollectionType is CollectionTypeOptions.movies or CollectionTypeOptions.tvshows && Guid.TryParse(folder.ItemId, out _))
            .Where(folder => root is null || !IsOnlyFederated(folder, root))
            .Select(folder => new ProviderLibrary(Guid.Parse(folder.ItemId).ToString("N"), folder.Name ?? string.Empty, folder.CollectionType!.Value == CollectionTypeOptions.movies ? "movies" : "tvshows"))
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
    /// Finds an item a peer may reach.
    /// </summary>
    /// <param name="caller">The peer.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>The item, null if it does not exist as far as the peer is concerned.</returns>
    public BaseItem? FindVisible(PeerCaller caller, Guid itemId)
    {
        ArgumentNullException.ThrowIfNull(caller);

        return _library.GetItemById<BaseItem>(itemId, caller.User) is { } item
            && !IsFederated(item)
            && SharedLibrariesOf(caller.Grant, item).Count > 0
                ? item
                : null;
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
        if (ScopeOf(libraryId) is not { } scope)
        {
            return new ProviderItemsResponse([], 0, new Dictionary<string, RemoteQuality>(StringComparer.Ordinal));
        }

        var query = new InternalItemsQuery(caller.User)
        {
            ParentId = libraryId,
            Recursive = true,
            IncludeItemTypes = [KindOf(type)],
            IsVirtualItem = false,
            AncestorIds = scope.AncestorIds,
            ExcludeItemIds = scope.ExcludeItemIds,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Ascending), (ItemSortBy.SortName, SortOrder.Ascending)],
            StartIndex = startIndex,
            Limit = limit,
            EnableTotalRecordCount = true
        };

        var page = _library.GetItemsResult(query);

        // Should a stub get past the query, the page comes up short: the read fails and nothing leaks.
        return ToResponse(caller, page.Items.Where(item => !IsFederated(item)).ToList(), page.TotalRecordCount);
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

        var kind = KindOf(type);
        var items = itemIds
            .Select(id => FindVisible(caller, id))
            .OfType<BaseItem>()
            .Where(item => item.GetBaseItemKind() == kind)
            .ToList();
        return ToResponse(caller, items, items.Count);
    }

    /// <summary>
    /// Tells whether an item is of a kind the catalog holds.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Whether it is a film, series, season or episode.</returns>
    public static bool IsCatalogItem(BaseItem item) => item is Movie or Series or Season or Episode;

    private static BaseItemKind KindOf(RemoteItemType type) => type switch
    {
        RemoteItemType.Movie => BaseItemKind.Movie,
        RemoteItemType.Series => BaseItemKind.Series,
        RemoteItemType.Season => BaseItemKind.Season,
        RemoteItemType.Episode => BaseItemKind.Episode,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

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
        }

        // The file of a chapter image is a path of this server too.
        foreach (var chapter in dtos.SelectMany(dto => dto.Chapters ?? []))
        {
            chapter.ImagePath = null;
        }

        return new ProviderItemsResponse(dtos, total, quality);
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
