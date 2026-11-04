using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderCatalog"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="dto">The DTO service.</param>
    /// <param name="store">The configuration store.</param>
    public ProviderCatalog(ILibraryManager library, IDtoService dto, IPluginConfigurationStore store)
    {
        _library = library;
        _dto = dto;
        _store = store;
    }

    /// <summary>
    /// Tells whether an item is one this server federates itself. §4.4: such an item never leaves it. The URL covers
    /// films and episodes wherever they are, and the path covers the series and seasons, which have no URL.
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

        var folder = _store.Read(config => config.FederatedFolderPath);
        if (folder.Length == 0 || string.IsNullOrEmpty(item.Path))
        {
            return false;
        }

        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return item.Path.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lists the film and series libraries of this server.
    /// </summary>
    /// <returns>The libraries.</returns>
    public IReadOnlyList<ProviderLibrary> ReadLibraries()
    {
        return _library.GetVirtualFolders()
            .Where(folder => folder.CollectionType is CollectionTypeOptions.movies or CollectionTypeOptions.tvshows && Guid.TryParse(folder.ItemId, out _))
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
        var query = new InternalItemsQuery(caller.User)
        {
            ParentId = libraryId,
            Recursive = true,
            IncludeItemTypes = [KindOf(type)],
            IsVirtualItem = false,
            ExcludeItemIds = FederatedItemIds(),
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

        return new ProviderItemsResponse(dtos, total, quality);
    }

    private Guid[] FederatedItemIds()
    {
        var root = _store.Read(config => config.FederatedFolderPath);
        if (root.Length == 0)
        {
            return [];
        }

        var ids = new List<Guid>();
        foreach (var path in new[] { FederatedFolderLayout.MoviesPath(root), FederatedFolderLayout.SeriesPath(root) })
        {
            if (_library.FindByPath(path, true) is { } folder)
            {
                ids.AddRange(_library.GetItemIds(new InternalItemsQuery { AncestorIds = [folder.Id], Recursive = true }));
            }
        }

        return ids.ToArray();
    }
}
