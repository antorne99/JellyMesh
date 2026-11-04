using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Dedup;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// <see cref="ILocalCatalogReader"/> through the Jellyfin library. Stubs are recognised by their path under the federated
/// folder.
/// </summary>
public sealed class LocalCatalogReader : ILocalCatalogReader
{
    private const string LocalServerId = "local";

    private readonly ILibraryManager _libraryManager;
    private readonly IPluginConfigurationStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalCatalogReader"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="store">The configuration store.</param>
    public LocalCatalogReader(ILibraryManager libraryManager, IPluginConfigurationStore store)
    {
        _libraryManager = libraryManager;
        _store = store;
    }

    /// <inheritdoc />
    public IReadOnlyList<CatalogItem> Read()
    {
        var stubPrefix = StubPrefix(_store.Read(config => config.FederatedFolderPath));
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false
        });

        var series = new Dictionary<Guid, BaseItem?>();
        return items
            .Where(item => IsRealCopy(item, stubPrefix))
            .Select(item => ToCatalogItem(item, series))
            .ToList();
    }

    private static string? StubPrefix(string folder)
    {
        return folder.Length == 0
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
    }

    private static bool IsRealCopy(BaseItem item, string? stubPrefix)
    {
        return !item.IsVirtualItem
            && !string.IsNullOrEmpty(item.Path)
            && (stubPrefix is null || !item.Path.StartsWith(stubPrefix, StringComparison.Ordinal));
    }

    private CatalogItem ToCatalogItem(BaseItem item, Dictionary<Guid, BaseItem?> series)
    {
        if (item is not Episode episode)
        {
            return new CatalogItem
            {
                ServerId = LocalServerId,
                ItemId = item.Id.ToString("N"),
                Kind = ContentKind.Movie,
                Title = item.Name,
                Year = item.ProductionYear,
                Ids = ContentIds.FromProviderIds(item.ProviderIds),
                Metadata = new ComparableMetadata
                {
                    OriginalTitle = item.OriginalTitle,
                    RunTimeTicks = item.RunTimeTicks,
                    Overview = item.Overview,
                    Genres = item.Genres,
                    Studios = item.Studios,
                    People = new Lazy<IReadOnlyList<string>>(() => _libraryManager.GetPeople(item).Select(person => person.Name).OfType<string>().ToList())
                }
            };
        }

        // Episode.Series reads a static service, so the series is looked up here instead.
        if (!series.TryGetValue(episode.SeriesId, out var parent))
        {
            parent = _libraryManager.GetItemById(episode.SeriesId);
            series[episode.SeriesId] = parent;
        }

        return new CatalogItem
        {
            ServerId = LocalServerId,
            ItemId = item.Id.ToString("N"),
            Kind = ContentKind.Episode,
            Title = parent?.Name ?? episode.SeriesName,
            Year = parent?.ProductionYear,
            Ids = ContentIds.FromProviderIds(parent?.ProviderIds),
            SeasonNumber = episode.ParentIndexNumber,
            EpisodeNumber = episode.IndexNumber
        };
    }
}
