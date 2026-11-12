using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// Maps what a remote sent to catalog entries. Copies field by field, so that nothing else can reach the file.
/// </summary>
public static class RemoteItemMapper
{
    private const string VideoStream = "Video";

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

        return new IndexedItem
        {
            ItemId = item.Id,
            Kind = KindOf(type),
            LibraryId = libraryId,
            LastSeenUtc = seenUtc,
            Name = item.Name,
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
            RunTimeTicks = item.RunTimeTicks,
            DateCreated = item.DateCreated,
            Etag = item.Etag,
            IndexNumber = item.IndexNumber,
            IndexNumberEnd = item.IndexNumberEnd,
            ParentIndexNumber = item.ParentIndexNumber,
            SeriesId = item.SeriesId,
            SeriesName = item.SeriesName,
            SeasonId = item.SeasonId,
            SeasonName = item.SeasonName,
            ImageTags = item.ImageTags ?? new Dictionary<string, string>(),
            BackdropImageTags = item.BackdropImageTags ?? [],
            Chapters = item.Chapters?.Select(chapter => new IndexedChapter(chapter.Name, chapter.StartPositionTicks)).ToList() ?? [],

            // Decision 11: a version that is the stub of another JellyMesh is not a source. An item left with no versions
            // is skipped by the dedup, and an incremental read replaces the entry, so one that changed leaves at once.
            Versions = item.MediaSources?.Where(source => !IsFederatedStub(source)).Select(ToVersion).ToList() ?? []
        };
    }

    private static IndexedItemKind KindOf(RemoteItemType type) => type switch
    {
        RemoteItemType.Movie => IndexedItemKind.Movie,
        RemoteItemType.Series => IndexedItemKind.Series,
        RemoteItemType.Season => IndexedItemKind.Season,
        RemoteItemType.Episode => IndexedItemKind.Episode,
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
            RunTimeTicks = source.RunTimeTicks,
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
