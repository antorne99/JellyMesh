using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// A <c>BaseItemDto</c> of a remote catalog. Every value is untrusted text.
/// </summary>
/// <remarks>
/// This is a whitelist: only what is listed here is read from the answer. File paths and stream URLs, which can hold a
/// remote address or a token, are left out on purpose. <see cref="Path"/> and <see cref="RemoteMediaSource.Path"/> are the
/// exceptions, and they go no further than the mapper. The place where a photo was taken is never read.
/// </remarks>
public sealed record RemoteItem
{
    /// <summary>Gets the item ID on the remote.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the name.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the original title.</summary>
    public string? OriginalTitle { get; init; }

    /// <summary>Gets the plot.</summary>
    public string? Overview { get; init; }

    /// <summary>Gets the taglines.</summary>
    public IReadOnlyList<string>? Taglines { get; init; }

    /// <summary>Gets the trailers. An entry can be null, and is then skipped.</summary>
    public IReadOnlyList<RemoteMediaUrl?>? RemoteTrailers { get; init; }

    /// <summary>Gets the genres.</summary>
    public IReadOnlyList<string>? Genres { get; init; }

    /// <summary>Gets the studios.</summary>
    public IReadOnlyList<RemoteNamedItem>? Studios { get; init; }

    /// <summary>Gets the tags.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Gets the cast and crew.</summary>
    public IReadOnlyList<RemotePerson>? People { get; init; }

    /// <summary>Gets the age rating.</summary>
    public string? OfficialRating { get; init; }

    /// <summary>Gets the community rating.</summary>
    public float? CommunityRating { get; init; }

    /// <summary>Gets the critic rating.</summary>
    public float? CriticRating { get; init; }

    /// <summary>Gets the release date.</summary>
    public DateTimeOffset? PremiereDate { get; init; }

    /// <summary>Gets the end date of a series.</summary>
    public DateTimeOffset? EndDate { get; init; }

    /// <summary>Gets the production year. Jellyfin does not send it on episodes.</summary>
    public int? ProductionYear { get; init; }

    /// <summary>Gets the status of a series.</summary>
    public string? Status { get; init; }

    /// <summary>Gets all the provider IDs. Empty on episodes, in the test data.</summary>
    public IReadOnlyDictionary<string, string?>? ProviderIds { get; init; }

    /// <summary>Gets the duration.</summary>
    public long? RunTimeTicks { get; init; }

    /// <summary>Gets when the item was added.</summary>
    public DateTimeOffset? DateCreated { get; init; }

    /// <summary>Gets the tag that changes with the item.</summary>
    public string? Etag { get; init; }

    /// <summary>Gets the season or episode number.</summary>
    public int? IndexNumber { get; init; }

    /// <summary>Gets the last episode of a file that holds several.</summary>
    public int? IndexNumberEnd { get; init; }

    /// <summary>Gets the season number of an episode.</summary>
    public int? ParentIndexNumber { get; init; }

    /// <summary>Gets the series of a season or an episode.</summary>
    public string? SeriesId { get; init; }

    /// <summary>Gets the series name of a season or an episode.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Gets the season of an episode.</summary>
    public string? SeasonId { get; init; }

    /// <summary>Gets the season name of an episode.</summary>
    public string? SeasonName { get; init; }

    /// <summary>Gets the image tags by image type.</summary>
    public IReadOnlyDictionary<string, string>? ImageTags { get; init; }

    /// <summary>Gets the backdrop image tags.</summary>
    public IReadOnlyList<string>? BackdropImageTags { get; init; }

    /// <summary>Gets the chapters.</summary>
    public IReadOnlyList<RemoteChapter>? Chapters { get; init; }

    /// <summary>Gets the playable versions.</summary>
    public IReadOnlyList<RemoteMediaSource>? MediaSources { get; init; }

    /// <summary>Gets the folder, album or item that holds this one.</summary>
    public string? ParentId { get; init; }

    /// <summary>Gets the file path on the remote. Only the mapper reads it, for the extension of a book or a photo.</summary>
    public string? Path { get; init; }

    /// <summary>Gets the album of a track or a music video, the album of a photo, or the folder of an audiobook part.</summary>
    public string? Album { get; init; }

    /// <summary>Gets the album of a track.</summary>
    public string? AlbumId { get; init; }

    /// <summary>Gets the tag of the album image of a track.</summary>
    public string? AlbumPrimaryImageTag { get; init; }

    /// <summary>Gets the artists of a track or a music video.</summary>
    public IReadOnlyList<string>? Artists { get; init; }

    /// <summary>Gets the first album artist of a track.</summary>
    public string? AlbumArtist { get; init; }

    /// <summary>Gets the album artists of a track.</summary>
    public IReadOnlyList<RemoteNamedItem>? AlbumArtists { get; init; }

    /// <summary>Gets whether a track has lyrics.</summary>
    public bool? HasLyrics { get; init; }

    /// <summary>Gets the width of a photo.</summary>
    public int? Width { get; init; }

    /// <summary>Gets the height of a photo.</summary>
    public int? Height { get; init; }

    /// <summary>Gets the camera maker of a photo.</summary>
    public string? CameraMake { get; init; }

    /// <summary>Gets the camera model of a photo.</summary>
    public string? CameraModel { get; init; }

    /// <summary>Gets the software that wrote a photo.</summary>
    public string? Software { get; init; }

    /// <summary>Gets the exposure time of a photo, in seconds.</summary>
    public double? ExposureTime { get; init; }

    /// <summary>Gets the focal length of a photo.</summary>
    public double? FocalLength { get; init; }

    /// <summary>Gets the aperture of a photo.</summary>
    public double? Aperture { get; init; }

    /// <summary>Gets the shutter speed of a photo.</summary>
    public double? ShutterSpeed { get; init; }

    /// <summary>Gets the orientation of a photo.</summary>
    public string? ImageOrientation { get; init; }

    /// <summary>Gets the ISO speed of a photo.</summary>
    public int? IsoSpeedRating { get; init; }
}
