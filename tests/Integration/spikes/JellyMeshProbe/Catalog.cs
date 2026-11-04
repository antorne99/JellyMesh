using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace JellyMesh.Probe;

/// <summary>
/// Stands in for the federation index. Values copy "JM Audio e Sottotitoli" as a
/// remote reports it, including the remote Index layout (external subtitles first).
/// RunTimeTicks is deliberately not the clip's real 595190000, so a value that
/// came from ffprobe can never be mistaken for one that came from the plugin.
/// </summary>
public static class Catalog
{
    public const long RunTimeTicks = 1_234_567_890;
    public const int Bitrate = 4_083_146;
    public const long Size = 30_378_097;
    public const string Container = "mkv";
    public const string DynamicUrl = "http://host.docker.internal:18221/JellyMesh/Play/dynamic";
    public const string RewrittenUrl = "http://host.docker.internal:18221/JellyMesh/Play/rewritten";

    private const string ModeFile = "/config/jmprobe.mode";
    private const string FederatedRoot = "/federata/";

    /// <summary>Read on every call, so most modes change without a restart.</summary>
    public static bool Has(string mode)
    {
        return File.Exists(ModeFile)
            && File.ReadAllText(ModeFile).Split(',', StringSplitOptions.TrimEntries).Contains(mode);
    }

    public static bool IsFederated(BaseItem item)
    {
        return item.Path is { } path
            && path.StartsWith(FederatedRoot, StringComparison.Ordinal)
            && path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Remote Index layout: external subtitles first, then video, audio, embedded subtitle.</summary>
    public static List<MediaStream> Streams(bool withExternal)
    {
        var streams = new List<MediaStream>();
        if (withExternal)
        {
            streams.Add(Subtitle(0, "srt", "eng", isExternal: true));
            streams.Add(Subtitle(1, "srt", "ita", isExternal: true));
        }

        streams.Add(new MediaStream
        {
            Index = 2,
            Type = MediaStreamType.Video,
            Codec = "h264",
            Profile = "High",
            Level = 40,
            BitDepth = 8,
            BitRate = 3_700_000,
            Width = 1920,
            Height = 1080,
            AspectRatio = "16:9",
            AverageFrameRate = 24,
            RealFrameRate = 24,
            PixelFormat = "yuv420p",
            IsAVC = true,
            RefFrames = 4,
            IsDefault = true,
            ColorTransfer = "bt709",
            ColorPrimaries = "bt709",
            ColorSpace = "bt709",
        });
        streams.Add(Audio(3, "ac3", "ita", 6, isDefault: true));
        streams.Add(Audio(4, "aac", "eng", 2, isDefault: false));
        streams.Add(Subtitle(5, "subrip", "ita", isExternal: false));
        return streams;
    }

    public static List<ChapterInfo> Chapters()
    {
        return
        [
            new ChapterInfo { Name = "Uno", StartPositionTicks = 0 },
            new ChapterInfo { Name = "Due", StartPositionTicks = 300_000_000 },
        ];
    }

    public static MediaSourceInfo Source(string id, string path, bool withExternal)
    {
        return new MediaSourceInfo
        {
            Id = id,
            Name = "JellyMesh probe source",
            Path = path,
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            Container = Container,
            Size = Size,
            Bitrate = Bitrate,
            RunTimeTicks = RunTimeTicks,
            VideoType = VideoType.VideoFile,
            MediaStreams = Streams(withExternal),
            SupportsProbing = false,
            RequiresOpening = false,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
        };
    }

    private static MediaStream Audio(int index, string codec, string language, int channels, bool isDefault)
    {
        return new MediaStream
        {
            Index = index,
            Type = MediaStreamType.Audio,
            Codec = codec,
            Language = language,
            Channels = channels,
            ChannelLayout = channels == 6 ? "5.1" : "stereo",
            SampleRate = 48_000,
            BitRate = 192_000,
            IsDefault = isDefault,
        };
    }

    private static MediaStream Subtitle(int index, string codec, string language, bool isExternal)
    {
        return new MediaStream
        {
            Index = index,
            Type = MediaStreamType.Subtitle,
            Codec = codec,
            Language = language,
            IsExternal = isExternal,
            Path = isExternal ? "http://host.docker.internal:18221/JellyMesh/Sub/" + language : null,
        };
    }
}
