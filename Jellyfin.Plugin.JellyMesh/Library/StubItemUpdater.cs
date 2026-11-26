using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Index;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Drawing;
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
    /// Applies metadata to an item. Without protection only title, year, IDs, the artists and album of a music video and the
    /// date of a home video are the plugin's: the rest belongs to the fetchers, except on a book or a photo.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="metadata">The metadata.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ApplyMetadataAsync(BaseItem item, StubMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(metadata);

        var writesAll = WriteFields(item, metadata);

        // Not MetadataEdit: on that reason Jellyfin rewrites any .nfo it finds, in its own format, inside the federated
        // folder, even with its metadata savers off (observed on 12.4).
        await SaveAsync(item, ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);

        if (writesAll)
        {
            await _library.UpdatePeopleAsync(item, metadata.People.Select(ToPerson).OfType<PersonInfo>().ToList(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the fields of a book and its lock, as the update does, for the provider before its first refresh (D62).
    /// Nothing is saved, and the authors stay with the update.
    /// </summary>
    /// <param name="book">The book.</param>
    /// <param name="metadata">The metadata.</param>
    /// <returns>Whether a field changed.</returns>
    public static bool WriteBook(Book book, StubMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(metadata);

        var before = BookFieldsOf(book);
        WriteFields(book, metadata);
        return !string.Equals(before, BookFieldsOf(book), StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes the fields of a photo, its size, its EXIF with no position and its lock, as the update does, for the providers
    /// of a refresh (D7, D26, D45). Nothing is saved.
    /// </summary>
    /// <param name="photo">The photo.</param>
    /// <param name="metadata">The metadata.</param>
    /// <returns>Whether a field changed.</returns>
    public static bool WritePhoto(Photo photo, StubMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(metadata);

        var before = PhotoFieldsOf(photo);
        WriteFields(photo, metadata);
        return !string.Equals(before, PhotoFieldsOf(photo), StringComparison.Ordinal);
    }

    // Writes the fields of an item without saving it, and tells whether its people are the plugin's too.
    private static bool WriteFields(BaseItem item, StubMetadata metadata)
    {
        item.Name = metadata.Title ?? item.Name;
        item.ProductionYear = metadata.Year ?? item.ProductionYear;
        item.ProviderIds = ProviderIdsOf(metadata.ProviderIds);
        if (item is Movie && !string.IsNullOrEmpty(metadata.TmdbCollectionId))
        {
            // Jellyfin sets it from tmdbcolid when it first reads the .nfo.
            item.SetProviderId(MetadataProvider.TmdbCollection, metadata.TmdbCollectionId);
        }

        var isSeries = item is Series;
        item.IsLocked = metadata.IsProtected && !isSeries;
        item.LockedFields = metadata.IsProtected && isSeries ? NfoDocument.SeriesLockedFields.ToArray() : [];

        if (item is Audio audio)
        {
            WriteTrack(audio, metadata);
        }

        // A book or a photo gets all its data also without protection, only unlocked (D62, D45).
        var writesAll = metadata.IsProtected || item is Book or Photo;
        if (writesAll)
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
        else if (metadata.PremiereDate is { } date)
        {
            // The date of a home video is copied in both modes.
            item.PremiereDate = date.UtcDateTime;
        }

        if (item is MusicVideo musicVideo)
        {
            musicVideo.Artists = metadata.Artists.ToArray();
            musicVideo.Album = metadata.Album;
        }

        if (item is Book book && writesAll)
        {
            book.SeriesName = metadata.SeriesName;
        }

        if (item is Photo photo)
        {
            WritePhotoFields(photo, metadata.Photo);
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

        return writesAll;
    }

    // The size and camera data of a photo; where it was taken is never kept (D26).
    private static void WritePhotoFields(Photo photo, IndexedPhoto? data)
    {
        photo.Latitude = null;
        photo.Longitude = null;
        photo.Altitude = null;
        if (data is null)
        {
            return;
        }

        photo.Width = data.Width ?? photo.Width;
        photo.Height = data.Height ?? photo.Height;
        photo.CameraMake = data.CameraMake;
        photo.CameraModel = data.CameraModel;
        photo.Software = data.Software;
        photo.ExposureTime = data.ExposureTime;
        photo.FocalLength = data.FocalLength;
        photo.Aperture = data.Aperture;
        photo.ShutterSpeed = data.ShutterSpeed;
        photo.IsoSpeedRating = data.IsoSpeedRating;
        photo.Orientation = Enum.TryParse<ImageOrientation>(data.ImageOrientation, true, out var orientation) && Enum.IsDefined(orientation) ? orientation : null;
    }

    // Keys that differ only by case keep the first in ordinal order, so that the same ids always give the same item.
    private static Dictionary<string, string> ProviderIdsOf(IReadOnlyDictionary<string, string> ids)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in ids.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            merged.TryAdd(key, value);
        }

        return merged;
    }

    /// <summary>
    /// Writes the fields of a track, or of an audiobook part with its plot, and its lock. Stock Jellyfin reads nothing from
    /// their stubs, so they are written with or without protection (D60, D69). Nothing is saved.
    /// </summary>
    /// <param name="audio">The track or part.</param>
    /// <param name="metadata">The metadata.</param>
    /// <returns>Whether a field changed.</returns>
    public static bool WriteTrack(Audio audio, StubMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(metadata);

        var before = TrackFieldsOf(audio);
        audio.Name = metadata.Title ?? audio.Name;
        audio.ProductionYear = metadata.Year ?? audio.ProductionYear;
        audio.ProviderIds = ProviderIdsOf(metadata.ProviderIds);
        audio.Album = metadata.Album;
        audio.Artists = metadata.Artists.ToArray();
        audio.AlbumArtists = metadata.AlbumArtists.ToArray();
        audio.IndexNumber = metadata.TrackNumber;
        audio.ParentIndexNumber = metadata.DiscNumber;
        audio.Genres = metadata.Genres.ToArray();
        audio.IsLocked = metadata.IsProtected;
        if (audio is AudioBook)
        {
            audio.Overview = metadata.Overview;
        }

        return !string.Equals(before, TrackFieldsOf(audio), StringComparison.Ordinal);
    }

    /// <summary>
    /// Applies the name and plot of a <c>season.nfo</c> to a season that exists. Its provider IDs stay the fetchers'.
    /// </summary>
    /// <param name="item">The season.</param>
    /// <param name="metadata">The metadata.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task ApplySeasonAsync(BaseItem item, StubMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(metadata);

        item.Name = metadata.Title ?? item.Name;
        if (metadata.IsProtected)
        {
            item.Overview = metadata.Overview;
        }

        item.LockedFields = metadata.IsProtected ? NfoDocument.SeasonLockedFields.ToArray() : [];
        return SaveAsync(item, ItemUpdateType.MetadataImport, cancellationToken);
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
    /// Drops an image from an item when it shows one of the given files. An image set by hand elsewhere stays.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="type">The image type.</param>
    /// <param name="paths">The files the plugin wrote for that image.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RemoveImageAsync(BaseItem item, ImageType type, IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(paths);

        var images = item.GetImages(type).Where(image => paths.Contains(image.Path, StringComparer.Ordinal)).ToList();
        if (images.Count > 0)
        {
            item.RemoveImages(images);
            await SaveAsync(item, ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
        }
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

    private static string TrackFieldsOf(Audio audio) => JsonSerializer.Serialize(new
    {
        audio.Name,
        audio.ProductionYear,
        ProviderIds = audio.ProviderIds.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToList(),
        audio.Album,
        audio.Artists,
        audio.AlbumArtists,
        audio.IndexNumber,
        audio.ParentIndexNumber,
        audio.Genres,
        audio.IsLocked,
        Plot = audio is AudioBook ? audio.Overview : null
    });

    private static string BookFieldsOf(Book book) => JsonSerializer.Serialize(new
    {
        book.Name,
        book.ProductionYear,
        ProviderIds = book.ProviderIds.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToList(),
        book.IsLocked,
        book.LockedFields,
        book.OriginalTitle,
        book.Overview,
        book.Tagline,
        book.Genres,
        book.Studios,
        book.Tags,
        book.OfficialRating,
        book.CommunityRating,
        book.CriticRating,
        book.PremiereDate,
        book.EndDate,
        book.SeriesName
    });

    private static string PhotoFieldsOf(Photo photo) => JsonSerializer.Serialize(new
    {
        photo.Name,
        photo.ProductionYear,
        ProviderIds = photo.ProviderIds.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToList(),
        photo.IsLocked,
        photo.LockedFields,
        photo.OriginalTitle,
        photo.Overview,
        photo.Tagline,
        photo.Genres,
        photo.Studios,
        photo.Tags,
        photo.OfficialRating,
        photo.CommunityRating,
        photo.CriticRating,
        photo.PremiereDate,
        photo.EndDate,
        photo.Width,
        photo.Height,
        photo.CameraMake,
        photo.CameraModel,
        photo.Software,
        photo.ExposureTime,
        photo.FocalLength,
        photo.Aperture,
        photo.ShutterSpeed,
        photo.IsoSpeedRating,
        photo.Orientation,
        photo.Latitude,
        photo.Longitude,
        photo.Altitude
    });

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
