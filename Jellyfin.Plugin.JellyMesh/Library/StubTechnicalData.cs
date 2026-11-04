using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyMesh.Index;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Writes on a stub item what ffprobe would otherwise find, from the index (decision 4). The stream layout is the remote's,
/// including its <c>Index</c> values, because the remote is the one that will serve them.
/// </summary>
public sealed class StubTechnicalData
{
    private const string ExternalPathScheme = "jellymesh-external-subtitle";

    private readonly IFederationIndex _index;
    private readonly IMediaStreamRepository _streams;
    private readonly IChapterManager _chapters;
    private volatile IndexItemLookup? _lookup;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubTechnicalData"/> class.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="streams">The stream repository.</param>
    /// <param name="chapters">The chapter manager.</param>
    public StubTechnicalData(IFederationIndex index, IMediaStreamRepository streams, IChapterManager chapters)
    {
        _index = index;
        _streams = streams;
        _chapters = chapters;
    }

    /// <summary>
    /// Maps an indexed stream.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <param name="stubId">The stub the stream belongs to.</param>
    /// <returns>The stream to store, null if its type is unknown.</returns>
    public static MediaStream? ToMediaStream(IndexedTrack stream, string stubId)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!Enum.TryParse<MediaStreamType>(stream.Type, true, out var type) || stream.Index is not { } index)
        {
            return null;
        }

        return new MediaStream
        {
            Type = type,
            Index = index,
            Codec = stream.Codec,
            Height = stream.Height,
            Width = stream.Width,
            BitRate = stream.BitRate,
            IsDefault = stream.IsDefault,
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

            // Jellyfin counts ffmpeg stream numbers among the streams with the same Path as the one it wants, so an
            // external stream with no Path shifts all the others. The value is a marker, never opened: a remote path
            // is not stored, and the playback endpoints serve these subtitles themselves.
            Path = stream.IsExternal ? string.Create(CultureInfo.InvariantCulture, $"{ExternalPathScheme}:{stubId}:{index}") : null,
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

    /// <summary>
    /// Writes the numbers of an episode. Nothing is saved on the item itself.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="stub">The stub.</param>
    /// <returns>Whether a number changed.</returns>
    public static bool WriteNumbers(BaseItem item, StubEntry stub)
    {
        ArgumentNullException.ThrowIfNull(stub);

        if (item is not Episode episode)
        {
            return false;
        }

        var changed = Set(episode.ParentIndexNumber, stub.SeasonNumber, value => episode.ParentIndexNumber = value);
        return Set(episode.IndexNumber, stub.EpisodeNumber, value => episode.IndexNumber = value) | changed;
    }

    /// <summary>
    /// Writes the item fields and the streams, and the numbers of an episode. Nothing is saved on the item itself.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="stub">The stub.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether a field of the item changed.</returns>
    public bool WriteItemAndStreams(BaseItem item, StubEntry stub, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(stub);

        var changed = WriteNumbers(item, stub);
        if (SourceOf(stub) is not { } source)
        {
            return changed;
        }

        var version = source.Version;
        changed |= Set(item.RunTimeTicks, version.RunTimeTicks ?? source.Item.RunTimeTicks, value => item.RunTimeTicks = value);
        changed |= Set(item.TotalBitrate, version.ContainerBitRate, value => item.TotalBitrate = value);
        changed |= Set(item.Container, version.Container, value => item.Container = value);

        if (item is Video video)
        {
            changed |= Set(video.Timestamp, ParseEnum<TransportStreamTimestamp>(version.Timestamp), value => video.Timestamp = value);
            changed |= Set(video.Video3DFormat, ParseEnum<Video3DFormat>(version.Video3DFormat), value => video.Video3DFormat = value);
        }

        SaveStreams(item, stub, version, cancellationToken);
        return changed;
    }

    /// <summary>
    /// Writes the streams and the chapters again, after <c>ProbeProvider</c> ran. Without a probe result it renumbers the
    /// streams, drops the external ones and stores an empty chapter list.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="stub">The stub.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public void WriteStreamsAndChapters(BaseItem item, StubEntry stub, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(stub);

        if (SourceOf(stub) is not { } source)
        {
            return;
        }

        SaveStreams(item, stub, source.Version, cancellationToken);
        var chapters = source.Item.Chapters
            .Select(chapter => new ChapterInfo { Name = chapter.Name, StartPositionTicks = chapter.StartPositionTicks })
            .ToList();
        _chapters.SaveChapters(item, chapters);
    }

    private static bool Set<T>(T current, T? wanted, Action<T> write)
    {
        if (wanted is null || EqualityComparer<T>.Default.Equals(current, wanted))
        {
            return false;
        }

        write(wanted);
        return true;
    }

    private static T? ParseEnum<T>(string? value)
        where T : struct, Enum
    {
        return Enum.TryParse<T>(value, true, out var parsed) ? parsed : null;
    }

    private void SaveStreams(BaseItem item, StubEntry stub, IndexedVersion version, CancellationToken cancellationToken)
    {
        var streams = version.Streams.Select(stream => ToMediaStream(stream, stub.Id)).OfType<MediaStream>().ToList();
        if (streams.Count > 0)
        {
            _streams.SaveMediaStreams(item.Id, streams, cancellationToken);
        }
    }

    // The sources are in order, so the first one the index knows is the best that can be described.
    private (IndexedItem Item, IndexedVersion Version)? SourceOf(StubEntry stub)
    {
        var lookup = CurrentLookup();
        foreach (var source in stub.Sources)
        {
            if (lookup.Find(source.ServerId, source.ItemId) is { } item)
            {
                var version = item.Versions.FirstOrDefault(entry => string.Equals(entry.MediaSourceId, source.MediaSourceId, StringComparison.Ordinal))
                    ?? (item.Versions.Count > 0 ? item.Versions[0] : null);
                if (version is not null)
                {
                    return (item, version);
                }
            }
        }

        return null;
    }

    private IndexItemLookup CurrentLookup()
    {
        var snapshot = _index.Snapshot;
        var lookup = _lookup;
        if (lookup is null || !ReferenceEquals(lookup.Snapshot, snapshot))
        {
            lookup = IndexItemLookup.Build(snapshot);
            _lookup = lookup;
        }

        return lookup;
    }
}
