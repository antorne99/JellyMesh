using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Dedup;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// One type folder of a remote library folder, with the type of the local library that reads it.
/// </summary>
/// <param name="Name">The folder name.</param>
/// <param name="LibraryType">The type of the local library that holds the folder.</param>
public sealed record TypeFolder(string Name, CollectionTypeOptions LibraryType)
{
    /// <summary>Gets the folder of films.</summary>
    public static TypeFolder Movies { get; } = new("Film", CollectionTypeOptions.movies);

    /// <summary>Gets the folder of series, with their seasons and episodes.</summary>
    public static TypeFolder Series { get; } = new("Serie", CollectionTypeOptions.tvshows);

    /// <summary>Gets the folder of music tracks.</summary>
    public static TypeFolder Music { get; } = new("Musica", CollectionTypeOptions.music);

    /// <summary>Gets the folder of music videos.</summary>
    public static TypeFolder MusicVideos { get; } = new("Video musicali", CollectionTypeOptions.musicvideos);

    /// <summary>Gets the folder of home videos and photos.</summary>
    public static TypeFolder HomeVideos { get; } = new("Video domestici", CollectionTypeOptions.homevideos);

    /// <summary>Gets the folder of ebooks and audiobooks.</summary>
    public static TypeFolder Books { get; } = new("Libri", CollectionTypeOptions.books);

    /// <summary>Gets every type folder, in a fixed order.</summary>
    public static IReadOnlyList<TypeFolder> All { get; } = [Movies, Series, Music, MusicVideos, HomeVideos, Books];

    /// <summary>Gets the type folders the plugin writes stubs into: films and series until the 0.7 types arrive.</summary>
    public static IReadOnlyList<TypeFolder> Written { get; } = [Movies, Series];

    /// <summary>Gives the type folder of a kind of stub.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The type folder.</returns>
    public static TypeFolder Of(StubKind kind) => kind switch
    {
        StubKind.Movie => Movies,
        StubKind.Episode => Series,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>Gives the type folder of a kind of content.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The type folder.</returns>
    public static TypeFolder Of(ContentKind kind) => kind switch
    {
        ContentKind.Movie => Movies,
        ContentKind.Episode => Series,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
