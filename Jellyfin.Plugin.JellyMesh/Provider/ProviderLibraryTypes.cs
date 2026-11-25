using System;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// The library types a peer can be given, each with the name and the item types the consumer reads from it
/// (<see cref="RemoteCollectionTypes"/>). A library without a type is mixed, read for films and series only (D14, D53).
/// </summary>
public static class ProviderLibraryTypes
{
    private static readonly (CollectionTypeOptions Type, string Name, RemoteItemType[] Items)[] _types =
    [
        (CollectionTypeOptions.movies, "movies", [RemoteItemType.Movie]),
        (CollectionTypeOptions.tvshows, "tvshows", [RemoteItemType.Series, RemoteItemType.Season, RemoteItemType.Episode]),
        (CollectionTypeOptions.music, "music", [RemoteItemType.Audio]),
        (CollectionTypeOptions.musicvideos, "musicvideos", [RemoteItemType.MusicVideo]),
        (CollectionTypeOptions.homevideos, "homevideos", [RemoteItemType.Video, RemoteItemType.Photo, RemoteItemType.PhotoAlbum, RemoteItemType.Folder]),
        (CollectionTypeOptions.books, "books", [RemoteItemType.Book, RemoteItemType.AudioBook]),
        (CollectionTypeOptions.mixed, "mixed", [RemoteItemType.Movie, RemoteItemType.Series, RemoteItemType.Season, RemoteItemType.Episode])
    ];

    /// <summary>
    /// Gets the name a peer is given for a library type, null for a type that is never shared.
    /// </summary>
    /// <param name="type">The library type, null for a library without one.</param>
    /// <returns>The name.</returns>
    public static string? NameOf(CollectionTypeOptions? type) => Find(type)?.Name;

    /// <summary>
    /// Tells whether a peer reads items of a type from a library of a type.
    /// </summary>
    /// <param name="type">The library type, null for a library without one.</param>
    /// <param name="item">The kind of the item.</param>
    /// <returns>Whether it does.</returns>
    public static bool Reads(CollectionTypeOptions? type, BaseItemKind item) =>
        TypeOf(item) is { } read && (Find(type)?.Items.Contains(read) ?? false);

    /// <summary>
    /// Tells whether a peer reads items of a type from any library.
    /// </summary>
    /// <param name="item">The item type.</param>
    /// <returns>Whether it does.</returns>
    public static bool Reads(RemoteItemType item) => _types.Any(type => type.Items.Contains(item));

    /// <summary>
    /// Tells whether a peer reads items of a kind from some library.
    /// </summary>
    /// <param name="kind">The kind of the item.</param>
    /// <returns>Whether it does.</returns>
    public static bool Reads(BaseItemKind kind) => TypeOf(kind) is { } type && Reads(type);

    /// <summary>
    /// Tells whether a library of a type gives a peer an item of a kind: one it reads from it, or the album of the tracks
    /// it reads, which reaches the peer as a cover. Any other kind it never gives.
    /// </summary>
    /// <param name="type">The library type, null for a library without one.</param>
    /// <param name="kind">The kind of the item.</param>
    /// <returns>Whether it does.</returns>
    public static bool Gives(CollectionTypeOptions? type, BaseItemKind kind) =>
        Reads(type, kind == BaseItemKind.MusicAlbum ? BaseItemKind.Audio : kind);

    /// <summary>
    /// Gets the kind of item a type is.
    /// </summary>
    /// <param name="type">The item type.</param>
    /// <returns>The kind.</returns>
    public static BaseItemKind KindOf(RemoteItemType type) =>
        Enum.IsDefined(type) ? Enum.Parse<BaseItemKind>(type.ToString()) : throw new ArgumentOutOfRangeException(nameof(type));

    private static RemoteItemType? TypeOf(BaseItemKind kind) => Enum.TryParse<RemoteItemType>(kind.ToString(), out var type) ? type : null;

    private static (CollectionTypeOptions Type, string Name, RemoteItemType[] Items)? Find(CollectionTypeOptions? type)
    {
        var wanted = type ?? CollectionTypeOptions.mixed;
        return _types.FirstOrDefault(entry => entry.Type == wanted) is { Name: not null } found ? found : null;
    }
}
