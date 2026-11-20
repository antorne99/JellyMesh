using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// A music track of a remote catalog, with what its identity and quality are read from (row 453).
/// </summary>
public sealed record TrackItem
{
    /// <summary>Gets the plugin's ID for the server holding the track.</summary>
    public required string ServerId { get; init; }

    /// <summary>Gets the item ID on that server.</summary>
    public required string ItemId { get; init; }

    /// <summary>Gets the track title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the album artists.</summary>
    public IReadOnlyList<string> AlbumArtists { get; init; } = [];

    /// <summary>Gets the track artists, which stand for the album artists when there are none.</summary>
    public IReadOnlyList<string> Artists { get; init; } = [];

    /// <summary>Gets the album name.</summary>
    public string? Album { get; init; }

    /// <summary>Gets the disc number, <c>ParentIndexNumber</c>. Missing counts as disc 1.</summary>
    public int? DiscNumber { get; init; }

    /// <summary>Gets the track number, <c>IndexNumber</c>.</summary>
    public int? TrackNumber { get; init; }

    /// <summary>Gets the duration.</summary>
    public long? RunTimeTicks { get; init; }

    /// <summary>Gets the MusicBrainz recording ID.</summary>
    public string? RecordingId { get; init; }

    /// <summary>Gets the MusicBrainz track ID, which differs between releases of one recording.</summary>
    public string? TrackId { get; init; }

    /// <summary>Gets the playable versions. A track without any is skipped.</summary>
    public IReadOnlyList<MediaVersion> Versions { get; init; } = [];

    /// <summary>Gets the versions as the sources the quality comparison works on.</summary>
    /// <returns>One source per version.</returns>
    public IEnumerable<RemoteSource> ToSources() =>
        Versions.Select(version => new RemoteSource(ServerId, ItemId, version.MediaSourceId, version.Quality));
}
