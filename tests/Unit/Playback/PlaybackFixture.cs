using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

internal static class PlaybackValues
{
    public const string LocalToken = "local-user-token";
    public const string RemoteToken = "remote-token-sentinel";
    public const string RemotePlaySession = "remoteplaysessionsentinel";
    public const string RemoteDevice = "jellymesh-remote-device-sentinel";
    public const string Master = "/videos/aaaa-bbbb/master.m3u8?DeviceId=" + RemoteDevice + "&MediaSourceId=ms&VideoCodec=vp9&AudioCodec=opus&PlaySessionId=" + RemotePlaySession + "&ApiKey=" + RemoteToken + "&TranscodeReasons=ContainerNotSupported";

    public const string MasterPlaylist =
        "#EXTM3U\n"
        + "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"Ita\",URI=\"ms/Subtitles/1/subtitles.m3u8?SegmentLength=30&ApiKey=" + RemoteToken + "\"\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=800000,CODECS=\"vp09.00.10.08,opus\",SUBTITLES=\"subs\"\n"
        + "main.m3u8?DeviceId=" + RemoteDevice + "&PlaySessionId=" + RemotePlaySession + "&ApiKey=" + RemoteToken + "\n";

    public const string VariantPlaylist =
        "#EXTM3U\n"
        + "#EXT-X-MAP:URI=\"hls1/main/-1.mp4?DeviceId=" + RemoteDevice + "&PlaySessionId=" + RemotePlaySession + "&runtimeTicks=0&actualSegmentLengthTicks=0\"\n"
        + "#EXTINF:3.0,\n"
        + "hls1/main/0.mp4?DeviceId=" + RemoteDevice + "&PlaySessionId=" + RemotePlaySession + "&runtimeTicks=0&actualSegmentLengthTicks=30000000\n";

    public static readonly DeviceProfile WebProfile = new()
    {
        TranscodingProfiles =
        [
            new TranscodingProfile { Type = DlnaProfileType.Video, Protocol = MediaStreamProtocol.hls, VideoCodec = "av1,vp9", AudioCodec = "opus,flac", Container = "mp4" }
        ]
    };

    public static StubSource Source(string item = "remote-item") => new(Guid.NewGuid(), item, item + "-ms");

    public static StubEntry Stub(params StubSource[] sources) => new()
    {
        Id = "stub",
        Kind = StubKind.Movie,
        RelativePath = "Film/x/x.strm",
        Sources = sources,
        IdentityKey = "key"
    };

    public static PlaybackIdentity Identity() => new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "device", Guid.Parse("22222222-2222-2222-2222-222222222222"), "stub");

    public static PlaybackSession Session(PlaybackStreamKind kind = PlaybackStreamKind.Hls, TimeProvider? clock = null)
    {
        return new PlaybackSession(Identity(), Source(), RemotePlaySession, kind, PlaybackUrls.WithoutToken(Master), (clock ?? TimeProvider.System).GetUtcNow());
    }

    public static MediaSourceInfo HlsAnswer(string id) => new()
    {
        Id = id,
        Path = "/media/film/secret-remote-path.mkv",
        Container = "mkv",
        ETag = "remote-etag",
        SupportsDirectPlay = false,
        SupportsTranscoding = true,
        TranscodingUrl = Master,
        TranscodingSubProtocol = MediaStreamProtocol.hls,
        TranscodingContainer = "mp4",
        MediaStreams =
        [
            new MediaStream { Index = 0, Type = MediaStreamType.Subtitle, IsExternal = true, Path = "/media/film/secret.srt", DeliveryUrl = "/Videos/remote-item/ms/Subtitles/0/0/Stream.vtt?ApiKey=" + RemoteToken },
            new MediaStream { Index = 1, Type = MediaStreamType.Video, Codec = "h264" }
        ],
        MediaAttachments = [new MediaAttachment { Index = 7, DeliveryUrl = "/Videos/remote-item/ms/Attachments/7" }]
    };
}

/// <summary>Runs the call at once, or fails as a server that is offline.</summary>
internal sealed class FakeRegistry : IRemoteServerRegistry
{
    private static readonly RemoteEndpoint _endpoint = new(new Uri("http://friend.tailnet.ts.net:8096/"), PlaybackValues.RemoteDevice);
    private static readonly RemoteSession _session = new(PlaybackValues.RemoteToken, "remote-user");

    public HashSet<Guid> Offline { get; } = [];

    public RemoteFailure OfflineFailure { get; set; } = RemoteFailure.Unreachable;

    public List<RemoteServerInfo> Servers { get; } = [];

    public IReadOnlyList<RemoteServerInfo> GetServers() => Servers;

    public Task<RemoteServerInfo?> RefreshCapabilitiesAsync(Guid serverId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public void AddJellyMesh(Guid serverId)
    {
        Servers.Add(new RemoteServerInfo(serverId, "peer", "http://peer", RemoteMode.JellyMesh, 1, [], null, null));
    }

    public Task<RemoteResult<RemoteServerInfo>> AddAsync(string name, string url, RemoteCredentials credentials, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<RemoteResult<RemoteServerInfo>> UpdateCredentialsAsync(Guid serverId, RemoteCredentials credentials, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Action? OnRemove { get; set; }

    public Task<bool> RemoveAsync(Guid serverId)
    {
        OnRemove?.Invoke();
        return Task.FromResult(Servers.RemoveAll(server => server.Id == serverId) > 0);
    }

    public Task<RemoteResult<ConnectionReport>> TestAsync(Guid serverId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<RemoteResult<IReadOnlyList<RemoteLibrary>>> GetLibrariesAsync(Guid serverId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public bool SetImportedLibraries(Guid serverId, IEnumerable<string> libraryIds) => throw new NotSupportedException();

    public bool Rename(Guid serverId, string name) => throw new NotSupportedException();

    public Task<RemoteResult<T>> RunWithSessionAsync<T>(Guid serverId, Func<RemoteEndpoint, RemoteSession, CancellationToken, Task<RemoteResult<T>>> call, CancellationToken cancellationToken)
    {
        return Offline.Contains(serverId)
            ? Task.FromResult(RemoteResult.Fail<T>(OfflineFailure))
            : call(_endpoint, _session, cancellationToken);
    }

    public Task<RemoteResult<T>> RunPlaybackCallAsync<T>(Guid serverId, Func<RemoteEndpoint, RemoteSession, CancellationToken, Task<RemoteResult<T>>> call, CancellationToken cancellationToken)
        => RunWithSessionAsync(serverId, call, cancellationToken);
}

/// <summary>A remote scripted per test. Records what is asked of it, per item path.</summary>
internal sealed class FakePlaybackClient : IRemotePlaybackClient
{
    public List<RemotePlaybackRequest> Requests { get; } = [];

    public List<string> Opened { get; } = [];

    public List<string> Stopped { get; } = [];

    public Func<RemotePlaybackRequest, CancellationToken, Task<RemoteResult<PlaybackInfoResponse>>> Answer { get; set; } = (request, _) => Task.FromResult(Hls(request));

    public Func<string, RemoteFailure> Playlists { get; set; } = _ => RemoteFailure.None;

    public string Master { get; set; } = PlaybackValues.MasterPlaylist;

    public Func<RemoteStreamRequest, CancellationToken, Task<RemoteResult<RemoteContent>>> Open { get; set; } = (_, _) => Task.FromResult(Bytes());

    public RemoteTranscodePolicy Policy { get; set; } = new("none", true, true, true);

    public int PolicyReads { get; private set; }

    public Action? OnStop { get; set; }

    public static RemoteResult<PlaybackInfoResponse> Hls(RemotePlaybackRequest request)
    {
        return RemoteResult.Success(new PlaybackInfoResponse { PlaySessionId = PlaybackValues.RemotePlaySession + request.ItemId, MediaSources = [PlaybackValues.HlsAnswer(request.MediaSourceId)] });
    }

    public static RemoteResult<PlaybackInfoResponse> Direct(RemotePlaybackRequest request)
    {
        var source = PlaybackValues.HlsAnswer(request.MediaSourceId);
        source.SupportsDirectPlay = true;
        source.TranscodingUrl = null;
        return RemoteResult.Success(new PlaybackInfoResponse { PlaySessionId = PlaybackValues.RemotePlaySession, MediaSources = [source] });
    }

    public static RemoteResult<RemoteContent> Bytes()
    {
        return RemoteResult.Success(new RemoteContent(HttpStatusCode.OK, new RemoteContentHeaders("video/mp4", 1, null, null), new MemoryStream([1])));
    }

    public Task<RemoteResult<PlaybackInfoResponse>> GetPlaybackInfoAsync(RemoteEndpoint endpoint, RemoteSession session, RemotePlaybackRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Answer(request, cancellationToken);
    }

    public List<bool> HardwareReads { get; } = [];

    public Task<RemoteResult<RemoteTranscodePolicy>> GetTranscodePolicyAsync(RemoteEndpoint endpoint, RemoteSession session, bool readHardware, CancellationToken cancellationToken)
    {
        HardwareReads.Add(readHardware);
        PolicyReads++;
        return Task.FromResult(RemoteResult.Success(Policy));
    }

    public Task<RemoteResult<string>> ReadPlaylistAsync(RemoteEndpoint endpoint, RemoteSession session, string path, CancellationToken cancellationToken)
    {
        var failure = Playlists(path);
        if (failure != RemoteFailure.None)
        {
            return Task.FromResult(RemoteResult.Fail<string>(failure));
        }

        return Task.FromResult(RemoteResult.Success(path.Contains("master.m3u8", StringComparison.Ordinal) ? Master : PlaybackValues.VariantPlaylist));
    }

    public Task<RemoteResult<RemoteContent>> OpenStreamAsync(RemoteEndpoint endpoint, RemoteSession session, RemoteStreamRequest request, CancellationToken cancellationToken)
    {
        Opened.Add(request.Path);
        return Open(request, cancellationToken);
    }

    public Task<RemoteResult<bool>> StopEncodingAsync(RemoteEndpoint endpoint, RemoteSession session, string playSessionId, CancellationToken cancellationToken)
    {
        Stopped.Add(playSessionId);
        OnStop?.Invoke();
        return Task.FromResult(RemoteResult.Success(true));
    }

    public List<string> Renewed { get; } = [];

    public RemoteFailure RenewalFailure { get; set; } = RemoteFailure.None;

    public Task<RemoteResult<bool>> RenewStreamAsync(RemoteEndpoint endpoint, RemoteSession session, string playSessionId, CancellationToken cancellationToken)
    {
        Renewed.Add(playSessionId);
        return Task.FromResult(RenewalFailure == RemoteFailure.None ? RemoteResult.Success(true) : RemoteResult.Fail<bool>(RenewalFailure));
    }

    public List<string> Released { get; } = [];

    public RemoteFailure ReleaseFailure { get; set; } = RemoteFailure.None;

    public Task<RemoteResult<bool>> ReleaseStreamAsync(RemoteEndpoint endpoint, RemoteSession session, string playSessionId, CancellationToken cancellationToken)
    {
        Released.Add(playSessionId);
        return Task.FromResult(ReleaseFailure == RemoteFailure.None ? RemoteResult.Success(true) : RemoteResult.Fail<bool>(ReleaseFailure));
    }
}

/// <summary>A clock moved by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan time) => _now += time;

    public override DateTimeOffset GetUtcNow() => _now;
}

internal sealed class CoordinatorFixture
{
    public CoordinatorFixture(ILastServedSourceStore? lastServed = null)
    {
        LastServed = lastServed ?? new InMemoryLastServedStore();
        Coordinator = new PlaybackCoordinator(Registry, Client, Sessions, Store, LastServed, Clock, NullLogger<PlaybackCoordinator>.Instance);
    }

    public ILastServedSourceStore LastServed { get; }

    public FakeRegistry Registry { get; } = new();

    public FakePlaybackClient Client { get; } = new();

    public PlaybackSessionStore Sessions { get; } = new();

    public InMemoryConfigurationStore Store { get; } = new();

    public ManualClock Clock { get; } = new();

    public PlaybackCoordinator Coordinator { get; }

    public Task<PlaybackOutcome> OpenAsync(StubEntry stub, DeviceProfile? profile = null, int? audio = null)
    {
        return Coordinator.OpenAsync(
            new PlaybackOpenRequest
            {
                Identity = PlaybackValues.Identity(),
                Stub = stub,
                LocalToken = PlaybackValues.LocalToken,
                DeviceProfile = profile,
                AudioStreamIndex = audio
            },
            CancellationToken.None);
    }
}
