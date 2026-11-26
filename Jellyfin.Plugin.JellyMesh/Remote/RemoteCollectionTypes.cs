using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// The remote library types the plugin imports, and the item types read from each (§3.12).
/// </summary>
public static class RemoteCollectionTypes
{
    /// <summary>Films.</summary>
    public const string Movies = "movies";

    /// <summary>Series.</summary>
    public const string TvShows = "tvshows";

    /// <summary>Music.</summary>
    public const string Music = "music";

    /// <summary>Music videos.</summary>
    public const string MusicVideos = "musicvideos";

    /// <summary>Home videos and photos.</summary>
    public const string HomeVideos = "homevideos";

    /// <summary>Ebooks, PDFs, comics and audiobooks.</summary>
    public const string Books = "books";

    /// <summary>A library with no type of its own, which Jellyfin sends without one [D14].</summary>
    public const string Mixed = "mixed";

    private const string Photos = "photos";
    private const string LibraryView = "CollectionFolder";

    private static readonly RemoteItemType[] _films = [RemoteItemType.Movie];
    private static readonly RemoteItemType[] _series = [RemoteItemType.Series, RemoteItemType.Season, RemoteItemType.Episode];

    private static readonly Dictionary<string, RemoteItemType[]> _itemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [Movies] = _films,
        [TvShows] = _series,
        [Mixed] = [.. _films, .. _series],
        [Music] = [RemoteItemType.Audio],
        [MusicVideos] = [RemoteItemType.MusicVideo],
        [HomeVideos] = [RemoteItemType.Video, RemoteItemType.Photo, RemoteItemType.PhotoAlbum, RemoteItemType.Folder],
        [Books] = [RemoteItemType.Book, RemoteItemType.AudioBook]
    };

    /// <summary>
    /// Gets the item types read from a library of a type, none for a type the plugin does not import.
    /// </summary>
    /// <param name="collectionType">The library type, as kept.</param>
    /// <returns>The item types, in the order they are read.</returns>
    public static IReadOnlyList<RemoteItemType> ItemTypesOf(string? collectionType)
        => collectionType is not null && _itemTypes.TryGetValue(collectionType, out var types) ? types : [];

    /// <summary>
    /// Gets the type a remote view is imported as: an old <c>photos</c> view as home videos, a library view without a
    /// type as mixed. Channels, playlists, collections and other views are no library the plugin imports.
    /// </summary>
    /// <param name="collectionType">The <c>CollectionType</c> of the view.</param>
    /// <param name="viewType">The <c>Type</c> of the view.</param>
    /// <returns>The type, or null.</returns>
    public static string? Of(string? collectionType, string? viewType)
    {
        if (string.IsNullOrEmpty(collectionType))
        {
            return string.Equals(viewType, LibraryView, StringComparison.Ordinal) ? Mixed : null;
        }

        var type = string.Equals(collectionType, Photos, StringComparison.OrdinalIgnoreCase) ? HomeVideos : collectionType.ToLowerInvariant();
        return _itemTypes.ContainsKey(type) ? type : null;
    }
}
