using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// Maps what a remote sent to catalog entries. Copies field by field, so that nothing else can reach the file.
/// </summary>
public static class RemoteItemMapper
{
    private const string VideoStream = "Video";

    // The book formats Jellyfin reads, and the image formats of a photo; nothing that means more to Jellyfin as a file.
    private static readonly FrozenSet<string> _bookExtensions = FrozenSet.Create(StringComparer.Ordinal, "azw", "azw3", "cb7", "cbr", "cbt", "cbz", "epub", "mobi", "pdf");

    private static readonly FrozenSet<string> _photoExtensions = new[]
    {
        "jpg", "jpeg", "png", "gif", "webp", "bmp", "tif", "tiff", "heic", "heif", "avif", "dng", "cr2", "nef", "arw", "orf", "rw2", "raf", "pef", "srw"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Maps an item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="type">The type asked for.</param>
    /// <param name="libraryId">The library it was read from.</param>
    /// <param name="seenUtc">When it was read.</param>
    /// <returns>The entry.</returns>
    public static IndexedItem ToIndexedItem(RemoteItem item, RemoteItemType type, string libraryId, DateTimeOffset seenUtc)
    {
        ArgumentNullException.ThrowIfNull(item);

        var kind = KindOf(type);
        var entry = new IndexedItem
        {
            ItemId = item.Id,
            Kind = kind,
            LibraryId = libraryId,
            LastSeenUtc = seenUtc,
            Name = RemoteName.Cut(item.Name),
            OriginalTitle = item.OriginalTitle,
            Overview = item.Overview,
            Taglines = item.Taglines ?? [],
            TrailerIds = YouTubeTrailer.IdsOf(item.RemoteTrailers),
            Genres = item.Genres ?? [],
            Studios = item.Studios?.Select(studio => studio.Name).OfType<string>().ToList() ?? [],
            Tags = item.Tags ?? [],
            People = item.People?.Select(person => new IndexedPerson(person.Name, person.Role, person.Type)).ToList() ?? [],
            OfficialRating = item.OfficialRating,
            CommunityRating = item.CommunityRating,
            CriticRating = item.CriticRating,
            PremiereDate = item.PremiereDate,
            EndDate = item.EndDate,
            ProductionYear = item.ProductionYear,
            Status = item.Status,
            ProviderIds = ProviderIdsOf(item),
            RunTimeTicks = item.RunTimeTicks is > 0 ? item.RunTimeTicks : null,
            DateCreated = item.DateCreated,
            Etag = item.Etag,
            IndexNumber = item.IndexNumber,
            IndexNumberEnd = item.IndexNumberEnd,
            ParentIndexNumber = item.ParentIndexNumber,
            SeriesId = item.SeriesId,
            SeriesName = RemoteName.Cut(item.SeriesName),
            SeasonId = item.SeasonId,
            SeasonName = RemoteName.Cut(item.SeasonName),
            ImageTags = item.ImageTags ?? new Dictionary<string, string>(),
            BackdropImageTags = item.BackdropImageTags ?? [],
            Chapters = item.Chapters?.Select(chapter => new IndexedChapter(chapter.Name, chapter.StartPositionTicks)).ToList() ?? [],

            // Decision 11: a version that is the stub of another JellyMesh is not a source. An item left with no versions
            // is skipped by the dedup, and an incremental read replaces the entry, so one that changed leaves at once.
            // A track or audiobook stub of another JellyMesh has its file path, not its URL, so it is told by its folder (row 455).
            Versions = AudioFolderOf(kind) is { } audioFolder && FederatedTrackMark.IsStub(item.Path, item.MediaSources, audioFolder)
                ? []
                : item.MediaSources?.Where(source => !IsFederatedStub(source)).Select(ToVersion).ToList() ?? []
        };

        // Films and series keep their stored form, so that their entries are not written again.
        return kind is IndexedItemKind.Movie or IndexedItemKind.Series or IndexedItemKind.Season or IndexedItemKind.Episode
            ? entry
            : WithNewTypeFields(entry, item);
    }

    // Adds the fields of music, home videos, photos and books. A book placeholder of another JellyMesh gets no
    // extension, so it gets no placeholder here, and an incremental read replaces the entry (D62).
    private static IndexedItem WithNewTypeFields(IndexedItem entry, RemoteItem item) => entry with
    {
        ParentId = item.ParentId,
        Album = RemoteName.Cut(item.Album),
        AlbumId = item.AlbumId,
        AlbumPrimaryImageTag = item.AlbumPrimaryImageTag,
        Artists = NullIfEmpty(item.Artists?.OfType<string>().Select(artist => RemoteName.Cut(artist)).ToList()),
        AlbumArtists = NullIfEmpty(item.AlbumArtists?.Select(artist => RemoteName.Cut(artist.Name)).OfType<string>().ToList())
            ?? (string.IsNullOrEmpty(item.AlbumArtist) ? null : [RemoteName.Cut(item.AlbumArtist)]),
        HasLyrics = item.HasLyrics,
        Extension = entry.Kind switch
        {
            IndexedItemKind.Book => FederatedBookMark.IsPlaceholder(item.Path, item.ProviderIds?.Keys) ? null : ExtensionOf(item.Path, _bookExtensions),
            IndexedItemKind.Photo => ExtensionOf(item.Path, _photoExtensions),
            _ => null
        },
        Photo = entry.Kind is IndexedItemKind.Photo ? PhotoOf(item) : null
    };

    private static TypeFolder? AudioFolderOf(IndexedItemKind kind) => kind switch
    {
        IndexedItemKind.Audio => TypeFolder.Music,
        IndexedItemKind.AudioBook => TypeFolder.Books,
        _ => null
    };

    private static List<string>? NullIfEmpty(List<string>? values) => values is { Count: > 0 } ? values : null;

    // Latitude, longitude and altitude are not read at all.
    private static IndexedPhoto PhotoOf(RemoteItem item) => new()
    {
        Width = item.Width,
        Height = item.Height,
        CameraMake = item.CameraMake,
        CameraModel = item.CameraModel,
        Software = item.Software,
        ExposureTime = item.ExposureTime,
        FocalLength = item.FocalLength,
        Aperture = item.Aperture,
        ShutterSpeed = item.ShutterSpeed,
        ImageOrientation = item.ImageOrientation,
        IsoSpeedRating = item.IsoSpeedRating
    };

    // Gets the lowercase extension of the last path segment when it is one of the allowed ones, else null.
    private static string? ExtensionOf(string? path, FrozenSet<string> allowed)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        var dot = name.LastIndexOf('.');
        var extension = dot < 0 ? string.Empty : name[(dot + 1)..];
        return extension.Length is >= 1 and <= 5 && extension.All(char.IsAsciiLetterOrDigit) && allowed.Contains(extension.ToLowerInvariant())
            ? extension.ToLowerInvariant()
            : null;
    }

    private static IndexedItemKind KindOf(RemoteItemType type) => type switch
    {
        RemoteItemType.Movie => IndexedItemKind.Movie,
        RemoteItemType.Series => IndexedItemKind.Series,
        RemoteItemType.Season => IndexedItemKind.Season,
        RemoteItemType.Episode => IndexedItemKind.Episode,
        RemoteItemType.Audio => IndexedItemKind.Audio,
        RemoteItemType.MusicVideo => IndexedItemKind.MusicVideo,
        RemoteItemType.Video => IndexedItemKind.Video,
        RemoteItemType.Photo => IndexedItemKind.Photo,
        RemoteItemType.PhotoAlbum => IndexedItemKind.PhotoAlbum,
        RemoteItemType.Folder => IndexedItemKind.Folder,
        RemoteItemType.Book => IndexedItemKind.Book,
        RemoteItemType.AudioBook => IndexedItemKind.AudioBook,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static Dictionary<string, string> ProviderIdsOf(RemoteItem item)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (provider, value) in item.ProviderIds ?? new Dictionary<string, string?>())
        {
            if (!string.IsNullOrEmpty(value))
            {
                ids[provider] = value;
            }
        }

        return ids;
    }

    private static bool IsFederatedStub(RemoteMediaSource source) => PlaybackRoutes.IsPlaybackEndpoint(source.Path);

    private static IndexedVersion ToVersion(RemoteMediaSource source)
    {
        var videos = source.MediaStreams?
            .Where(stream => string.Equals(stream.Type, VideoStream, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
        var video = videos.Find(stream => stream.IsDefault) ?? videos.FirstOrDefault();

        // A JellyMesh provider sends what it worked out itself. The same values, without guessing which stream it meant.
        var quality = source.Quality
            ?? (video is null ? null : new RemoteQuality(video.Height, video.Width, video.BitRate, video.Codec, video.VideoRange, video.VideoRangeType));

        return new IndexedVersion
        {
            MediaSourceId = source.Id,
            Name = source.Name,
            Container = source.Container,
            Size = source.Size,
            ContainerBitRate = source.Bitrate,
            RunTimeTicks = source.RunTimeTicks is > 0 ? source.RunTimeTicks : null,
            ETag = source.ETag,
            Protocol = source.Protocol,
            IsRemote = source.IsRemote,
            Height = quality?.Height,
            Width = quality?.Width,
            Codec = quality?.Codec,
            BitRate = quality?.BitRate,
            VideoRange = quality?.VideoRange,
            VideoRangeType = quality?.VideoRangeType,
            Timestamp = source.Timestamp,
            Video3DFormat = source.Video3DFormat,
            HasSegments = source.HasSegments,
            Streams = source.MediaStreams?.Select(ToStream).ToList() ?? []
        };
    }

    private static IndexedTrack ToStream(RemoteStreamInfo stream)
    {
        return new IndexedTrack
        {
            Type = stream.Type,
            Codec = stream.Codec,
            Height = stream.Height,
            Width = stream.Width,
            BitRate = stream.BitRate,
            IsDefault = stream.IsDefault,
            Index = stream.Index,
            Language = stream.Language,
            Title = stream.Title,
            Profile = stream.Profile,
            Level = stream.Level,
            BitDepth = stream.BitDepth,
            AspectRatio = stream.AspectRatio,
            AverageFrameRate = stream.AverageFrameRate,
            RealFrameRate = stream.RealFrameRate,
            PixelFormat = stream.PixelFormat,
            IsAVC = stream.IsAVC,
            IsAnamorphic = stream.IsAnamorphic,
            IsInterlaced = stream.IsInterlaced,
            RefFrames = stream.RefFrames,
            CodecTag = stream.CodecTag,
            NalLengthSize = stream.NalLengthSize,
            TimeBase = stream.TimeBase,
            CodecTimeBase = stream.CodecTimeBase,
            Rotation = stream.Rotation,
            Comment = stream.Comment,
            Channels = stream.Channels,
            ChannelLayout = stream.ChannelLayout,
            SampleRate = stream.SampleRate,
            IsForced = stream.IsForced,
            IsHearingImpaired = stream.IsHearingImpaired,
            IsOriginal = stream.IsOriginal,
            IsExternal = stream.IsExternal,
            ColorRange = stream.ColorRange,
            ColorTransfer = stream.ColorTransfer,
            ColorPrimaries = stream.ColorPrimaries,
            ColorSpace = stream.ColorSpace,
            Hdr10PlusPresentFlag = stream.Hdr10PlusPresentFlag,
            DvVersionMajor = stream.DvVersionMajor,
            DvVersionMinor = stream.DvVersionMinor,
            DvProfile = stream.DvProfile,
            DvLevel = stream.DvLevel,
            RpuPresentFlag = stream.RpuPresentFlag,
            ElPresentFlag = stream.ElPresentFlag,
            BlPresentFlag = stream.BlPresentFlag,
            DvBlSignalCompatibilityId = stream.DvBlSignalCompatibilityId
        };
    }
}
