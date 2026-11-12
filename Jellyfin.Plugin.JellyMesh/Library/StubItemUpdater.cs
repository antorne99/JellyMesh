using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Index;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Applies a change to an item that already exists. Jellyfin reads a <c>.nfo</c> only when it creates the item, and a
/// locked item ignores its files, so a changed file alone would never be seen (plan §3.1).
/// </summary>
public sealed class StubItemUpdater
{
    private readonly ILibraryManager _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubItemUpdater"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    public StubItemUpdater(ILibraryManager library)
    {
        _library = library;
    }

    /// <summary>
    /// Applies metadata to an item. Without protection only title, year and IDs are the plugin's: the rest belongs to the
    /// fetchers.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="metadata">The metadata.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ApplyMetadataAsync(BaseItem item, StubMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(metadata);

        item.Name = metadata.Title ?? item.Name;
        item.ProductionYear = metadata.Year ?? item.ProductionYear;
        item.ProviderIds = new Dictionary<string, string>(metadata.ProviderIds, StringComparer.OrdinalIgnoreCase);

        var isSeries = item is Series;
        item.IsLocked = metadata.IsProtected && !isSeries;
        item.LockedFields = metadata.IsProtected && isSeries ? NfoDocument.SeriesLockedFields.ToArray() : [];

        if (metadata.IsProtected)
        {
            item.OriginalTitle = metadata.OriginalTitle;
            item.Overview = metadata.Overview;
            item.Tagline = metadata.Taglines.Count > 0 ? metadata.Taglines[0] : null;
            item.Genres = metadata.Genres.ToArray();
            item.Studios = metadata.Studios.ToArray();
            item.Tags = metadata.Tags.ToArray();
            item.OfficialRating = metadata.OfficialRating;
            item.CommunityRating = metadata.CommunityRating;
            item.CriticRating = metadata.CriticRating;
            item.PremiereDate = metadata.PremiereDate?.UtcDateTime;
            item.EndDate = metadata.EndDate?.UtcDateTime;
        }

        if (metadata.IsProtected && item is Movie or Series)
        {
            item.RemoteTrailers = metadata.TrailerIds.Select(id => new MediaUrl { Url = YouTubeTrailer.WatchPrefix + id }).ToArray();
        }

        // Unprotected films keep a collection the admin may have set; protected ones follow the remote.
        if (item is Movie movie && (metadata.CollectionName is not null || (metadata.WritesCollections && metadata.IsProtected)))
        {
            movie.CollectionName = metadata.CollectionName;
        }

        // Not MetadataEdit: on that reason Jellyfin rewrites any .nfo it finds, in its own format, inside the federated
        // folder, even with its metadata savers off (observed on 12.4).
        await SaveAsync(item, ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);

        if (metadata.IsProtected)
        {
            await _library.UpdatePeopleAsync(item, metadata.People.Select(ToPerson).OfType<PersonInfo>().ToList(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Points an item to the URL of its <c>.strm</c>. Jellyfin reads the file only when it refreshes the item, and until
    /// then it keeps opening the old URL.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="url">The URL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ApplyStrmUrlAsync(BaseItem item, string url, CancellationToken cancellationToken)
    {
        if (item is Video video && !string.Equals(video.ShortcutPath, url, StringComparison.Ordinal))
        {
            video.ShortcutPath = url;
            await SaveAsync(video, ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Points an item to an image written next to it.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="type">The image type.</param>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ApplyImageAsync(BaseItem item, ImageType type, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var image = new ItemImageInfo { Path = path, Type = type, DateModified = File.GetLastWriteTimeUtc(path) };
        if (item.GetImageInfo(type, 0) is null)
        {
            item.AddImage(image);
        }
        else
        {
            item.SetImage(image, 0);
        }

        await SaveAsync(item, ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves an item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="reason">What changed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task SaveAsync(BaseItem item, ItemUpdateType reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _library.UpdateItemAsync(item, _library.GetItemById(item.ParentId)!, reason, cancellationToken);
    }

    private static PersonInfo? ToPerson(IndexedPerson person)
    {
        if (string.IsNullOrWhiteSpace(person.Name))
        {
            return null;
        }

        return new PersonInfo
        {
            Name = person.Name,
            Role = person.Role,
            Type = Enum.TryParse<PersonKind>(person.Type, true, out var kind) ? kind : PersonKind.Unknown
        };
    }
}
