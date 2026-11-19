using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The type folders and the local type of each kind of plugin library (§3.7, [D14], [D15]).
/// </summary>
internal static class ServerLibraryKinds
{
    private static readonly IReadOnlyList<TypeFolder> _mixedFolders = [TypeFolder.Movies, TypeFolder.Series];

    /// <summary>Gets the kinds with one type folder, in the order their libraries are made: by type and shared.</summary>
    public static IReadOnlyList<ServerLibraryKind> OfOneType { get; } =
        [ServerLibraryKind.Movies, ServerLibraryKind.Series, ServerLibraryKind.Music, ServerLibraryKind.MusicVideos, ServerLibraryKind.HomeVideos, ServerLibraryKind.Books];

    /// <summary>Gets every kind, the mixed one included.</summary>
    public static IReadOnlyList<ServerLibraryKind> All { get; } = [.. OfOneType, ServerLibraryKind.Mixed];

    /// <summary>Gets the kinds the 0.5 layout has a folder of the server for.</summary>
    public static IReadOnlyList<ServerLibraryKind> OfOlderLayout { get; } = [ServerLibraryKind.Movies, ServerLibraryKind.Series];

    /// <summary>Gives the kind of a remote library type as kept, null for one the plugin makes no library for.</summary>
    /// <param name="collectionType">The remote library type.</param>
    /// <returns>The kind, or null.</returns>
    public static ServerLibraryKind? Of(string collectionType) => (collectionType ?? string.Empty).ToLowerInvariant() switch
    {
        "movies" => ServerLibraryKind.Movies,
        "tvshows" => ServerLibraryKind.Series,
        "music" => ServerLibraryKind.Music,
        "musicvideos" => ServerLibraryKind.MusicVideos,
        "homevideos" => ServerLibraryKind.HomeVideos,
        "books" => ServerLibraryKind.Books,
        "mixed" => ServerLibraryKind.Mixed,
        _ => null
    };

    /// <summary>Gives the type folder of a kind with one; the mixed kind has two.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The type folder.</returns>
    public static TypeFolder TypeFolderOf(ServerLibraryKind kind) => kind switch
    {
        ServerLibraryKind.Movies => TypeFolder.Movies,
        ServerLibraryKind.Series => TypeFolder.Series,
        ServerLibraryKind.Music => TypeFolder.Music,
        ServerLibraryKind.MusicVideos => TypeFolder.MusicVideos,
        ServerLibraryKind.HomeVideos => TypeFolder.HomeVideos,
        ServerLibraryKind.Books => TypeFolder.Books,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>Gives the type folders a library of a kind holds: Film and Serie for a mixed one.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The type folders.</returns>
    public static IReadOnlyList<TypeFolder> TypeFoldersOf(ServerLibraryKind kind) => kind == ServerLibraryKind.Mixed ? _mixedFolders : [TypeFolderOf(kind)];

    /// <summary>Gives the local type of a library of a kind, null for a mixed one.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The local type.</returns>
    public static CollectionTypeOptions? LibraryTypeOf(ServerLibraryKind kind) => kind == ServerLibraryKind.Mixed ? null : TypeFolderOf(kind).LibraryType;

    /// <summary>Gives the item type of a library of a kind, as a rename keeps it, null for a mixed one.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The collection type.</returns>
    public static CollectionType? CollectionTypeOf(ServerLibraryKind kind) => kind switch
    {
        ServerLibraryKind.Movies => CollectionType.movies,
        ServerLibraryKind.Series => CollectionType.tvshows,
        ServerLibraryKind.Music => CollectionType.music,
        ServerLibraryKind.MusicVideos => CollectionType.musicvideos,
        ServerLibraryKind.HomeVideos => CollectionType.homevideos,
        ServerLibraryKind.Books => CollectionType.books,
        _ => null
    };

    /// <summary>Tells whether a library has the type of a kind: no type and "mixed" are the same for a mixed one (§3.9).</summary>
    /// <param name="type">The type of the library.</param>
    /// <param name="kind">The kind.</param>
    /// <returns>Whether the type is that of the kind.</returns>
    public static bool HasTypeOf(CollectionTypeOptions? type, ServerLibraryKind kind) => kind == ServerLibraryKind.Mixed
        ? type is null or CollectionTypeOptions.mixed
        : type == LibraryTypeOf(kind);
}
