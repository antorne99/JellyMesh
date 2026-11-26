using MediaBrowser.Model.Lyrics;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// The lyrics of a remote track, rebuilt from the answer of the remote; none when it has no lyrics for the track.
/// </summary>
/// <param name="Lyrics">The lyrics, or null.</param>
public sealed record RemoteLyrics(LyricDto? Lyrics)
{
    /// <summary>
    /// Gets the answer of a remote that has no lyrics for the track.
    /// </summary>
    public static RemoteLyrics None { get; } = new((LyricDto?)null);
}
