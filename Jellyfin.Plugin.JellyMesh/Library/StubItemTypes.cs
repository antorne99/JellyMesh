using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The types of the Jellyfin items stubs become.
/// </summary>
public static class StubItemTypes
{
    /// <summary>Gives the type of the item a stub of a kind becomes, by which Jellyfin names its ID.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The item type.</returns>
    public static Type Of(StubKind kind) => kind switch
    {
        StubKind.Movie => typeof(Movie),
        StubKind.Episode => typeof(Episode),
        StubKind.Track => typeof(Audio),
        StubKind.MusicVideo => typeof(MusicVideo),
        StubKind.HomeVideo => typeof(Video),
        StubKind.Book => typeof(Book),
        StubKind.AudioBook => typeof(AudioBook),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
