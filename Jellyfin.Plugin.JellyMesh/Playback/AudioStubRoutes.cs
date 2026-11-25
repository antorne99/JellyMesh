using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Serves universal, stream and the HLS playlists of a track stub through a session: the parameters of the client become
/// a device profile, the remote decides direct play or transcode, and a repeated request reuses its session.
/// </summary>
public sealed class AudioStubRoutes
{
    // The type stock gives an HLS playlist; jellyfin-web picks its player by it on the HEAD of universal.
    private const string StockPlaylistType = "application/x-mpegURL";

    private const int LockCount = 256;

    // Longest wait for an opening of the same stripe; past it the client gets a clear error, not a hung request.
    private static readonly TimeSpan _lockWait = TimeSpan.FromSeconds(30);

    private readonly PlaybackCoordinator _coordinator;
    private readonly PlaybackSessionStore _sessions;
    private readonly PlaybackRefusal _refusal;
    private readonly TimeProvider _clock;
    private readonly SessionRelay _relay;
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, LockCount).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly ConcurrentDictionary<string, string> _opened = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioStubRoutes"/> class.
    /// </summary>
    /// <param name="coordinator">The coordinator.</param>
    /// <param name="sessions">The sessions.</param>
    /// <param name="refusal">The refusal of a playback.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="errorLogs">The limits of the errors in the log.</param>
    public AudioStubRoutes(PlaybackCoordinator coordinator, PlaybackSessionStore sessions, PlaybackRefusal refusal, TimeProvider clock, ILogger<AudioStubRoutes> logger, ErrorLogLimits? errorLogs = null)
    {
        _coordinator = coordinator;
        _sessions = sessions;
        _refusal = refusal;
        _clock = clock;
        _relay = new SessionRelay(coordinator, refusal, clock, logger, errorLogs);
    }

    private enum AudioRoute
    {
        None,
        Universal,
        Stream,
        Master,
        Variant
    }

    /// <summary>
    /// Serves a stock audio route for a track stub the user sees.
    /// </summary>
    /// <param name="context">The request, its arguments bound by stock.</param>
    /// <param name="access">Who asks, with the local token and device.</param>
    /// <param name="stub">The stub of the track.</param>
    /// <param name="copies">The other copies of the track the user sees, best first: where the playback goes when it cannot serve.</param>
    /// <returns>The answer; a route it does not serve is a bare 503.</returns>
    public async Task<IActionResult> ServeAsync(ActionExecutingContext context, AudioAccess access, StubEntry stub, IReadOnlyList<PlaybackCandidate> copies)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(stub);
        ArgumentNullException.ThrowIfNull(copies);

        var (controller, action) = StubLookup.ActionOf(context);
        var route = RouteOf(controller, action);
        if (route == AudioRoute.None)
        {
            return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
        }

        // Universal and stream answer with the type of the remote, which is never sniffed (row 561).
        var http = context.HttpContext;
        if (route is AudioRoute.Universal or AudioRoute.Stream)
        {
            http.Response.Headers.XContentTypeOptions = "nosniff";
        }

        var arguments = new Arguments(context.ActionArguments);
        var request = OpenRequest(route, arguments, access, stub, copies);
        var opened = await FindOrOpenAsync(Fingerprint(route, access, context.ActionArguments), request, http.RequestAborted).ConfigureAwait(false);
        if (opened.Session is not { } session)
        {
            return _refusal.Refuse(http, access.DeviceId, opened.Text);
        }

        using var scope = session.TryBeginRequest(_clock, http.RequestAborted);
        if (scope is null)
        {
            return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
        }

        var prefix = "../../" + PlaybackUrls.SessionRoute + "/" + session.Id + "/f/";
        var head = HttpMethods.IsHead(http.Request.Method);
        return (route, session.Kind) switch
        {
            (AudioRoute.Variant, PlaybackStreamKind.Hls) => await VariantAsync(http, session, prefix, access.Token).ConfigureAwait(false),
            (AudioRoute.Master or AudioRoute.Universal, PlaybackStreamKind.Hls) when head => new FileContentResult([], StockPlaylistType),
            (AudioRoute.Master or AudioRoute.Universal, PlaybackStreamKind.Hls)
                => await _relay.PlaylistAsync(http, session, session.UpstreamPath, session.UpstreamFolder(), prefix, access.Token, StockPlaylistType, http.RequestAborted).ConfigureAwait(false),
            (AudioRoute.Universal or AudioRoute.Stream, PlaybackStreamKind.Progressive) when head => new FileContentResult([], AudioTypeOf(session.UpstreamPath)),
            (AudioRoute.Universal or AudioRoute.Stream, PlaybackStreamKind.Direct or PlaybackStreamKind.Progressive)
                => await _relay.StreamAsync(http, session, session.UpstreamPath, http.RequestAborted).ConfigureAwait(false),
            _ => new StatusCodeResult(StatusCodes.Status503ServiceUnavailable)
        };
    }

    /// <summary>
    /// Serves the lyrics of a track stub the user sees from its remote: a remote that has none is a 404, and a remote that
    /// cannot answer is a clear error.
    /// </summary>
    /// <param name="http">The request.</param>
    /// <param name="deviceId">The device of the caller.</param>
    /// <param name="stub">The stub of the track.</param>
    /// <returns>The answer.</returns>
    public async Task<IActionResult> LyricsAsync(HttpContext http, string deviceId, StubEntry stub)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(stub);

        var read = await _coordinator.ReadLyricsAsync(stub, http.RequestAborted).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            var offline = read.Failure is RemoteFailure.Unreachable or RemoteFailure.Timeout or RemoteFailure.UnknownServer or RemoteFailure.Suspended;
            return _refusal.Refuse(http, deviceId, offline ? PlaybackRefusal.Text : PlaybackRefusal.FailedText);
        }

        return read.Value!.Lyrics is { } lyrics ? new OkObjectResult(lyrics) : new NotFoundResult();
    }

    // The type of a transcoded stream by its extension, an audio type only, since the extension comes from the remote.
    private static string AudioTypeOf(string path)
    {
        return MimeTypes.GetMimeType(path.Split('?')[0]) is { } type && type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? type : "audio/mpeg";
    }

    private static AudioRoute RouteOf(string controller, string action) => (controller, action) switch
    {
        ("UniversalAudio", "GetUniversalAudioStream") => AudioRoute.Universal,
        ("Audio", "GetAudioStream" or "GetAudioStreamByContainer") => AudioRoute.Stream,
        ("DynamicHls", "GetMasterHlsAudioPlaylist") => AudioRoute.Master,
        ("DynamicHls", "GetVariantHlsAudioPlaylist") => AudioRoute.Variant,
        _ => AudioRoute.None
    };

    // The same client asking the same thing reuses its session: HEAD then GET, a seek, a playlist asked again.
    private static string Fingerprint(AudioRoute route, AudioAccess access, IDictionary<string, object?> arguments)
    {
        var kind = route is AudioRoute.Master or AudioRoute.Variant ? "hls" : route.ToString();
        var values = arguments
            .Where(pair => pair.Value is not null)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + Format(pair.Value));
        return string.Join('&', new[] { access.UserId.ToString("N"), access.DeviceId, kind }.Concat(values));
    }

    private static string Format(object? value) => value switch
    {
        string[] list => string.Join(',', list),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? string.Empty
    };

    private static PlaybackOpenRequest OpenRequest(AudioRoute route, Arguments arguments, AudioAccess access, StubEntry stub, IReadOnlyList<PlaybackCandidate> copies)
    {
        var hls = route is AudioRoute.Master or AudioRoute.Variant;
        var profile = route switch
        {
            AudioRoute.Universal => UniversalProfile(arguments),
            AudioRoute.Stream => StreamProfile(arguments),
            _ => HlsProfile(arguments)
        };
        return new PlaybackOpenRequest
        {
            Identity = new PlaybackIdentity(access.UserId, access.DeviceId, access.ItemId, stub.Id),
            Stub = stub,
            Fallbacks = copies,
            LocalToken = access.Token,
            DeviceProfile = profile,
            StartTimeTicks = arguments.Value<long>("startTimeTicks"),
            MaxStreamingBitrate = route == AudioRoute.Universal
                ? arguments.Value<int>("maxStreamingBitrate")
                : arguments.Value<int>("audioBitRate") ?? arguments.Value<int>("maxStreamingBitrate"),
            MaxAudioChannels = arguments.Value<int>("maxAudioChannels"),
            EnableDirectPlay = hls ? false : null,
            EnableDirectStream = hls ? false : null
        };
    }

    // The profile stock universal builds from its parameters.
    private static DeviceProfile UniversalProfile(Arguments arguments)
    {
        var containers = arguments.Get<string[]>("container") ?? [];
        var protocol = arguments.Value<MediaStreamProtocol>("transcodingProtocol") ?? MediaStreamProtocol.http;
        var profile = new DeviceProfile
        {
            DirectPlayProfiles = containers
                .Select(container => container.Split('|', StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length > 0)
                .Select(parts => new DirectPlayProfile
                {
                    Type = DlnaProfileType.Audio,
                    Container = parts[0],
                    AudioCodec = parts.Length == 1 ? null : string.Join(',', parts.Skip(1))
                })
                .ToArray(),
            TranscodingProfiles =
            [
                new TranscodingProfile
                {
                    Type = DlnaProfileType.Audio,
                    Context = EncodingContext.Streaming,
                    Container = arguments.Get<string>("transcodingContainer") ?? "mp3",
                    AudioCodec = arguments.Get<string>("audioCodec") ?? "mp3",
                    Protocol = protocol,
                    MaxAudioChannels = arguments.Value<int>("transcodingAudioChannels")?.ToString(CultureInfo.InvariantCulture)
                }
            ]
        };
        profile.CodecProfiles = CodecProfiles(arguments, string.Join(',', containers), "maxAudioSampleRate");
        return profile;
    }

    // A stream asked in a container: played as it is if the file has it, else transcoded to it.
    private static DeviceProfile StreamProfile(Arguments arguments)
    {
        var container = arguments.Get<string>("container")?.TrimStart('.');
        var codec = arguments.Get<string>("audioCodec");
        return new DeviceProfile
        {
            DirectPlayProfiles = string.IsNullOrEmpty(container) ? [] : [new DirectPlayProfile { Type = DlnaProfileType.Audio, Container = container, AudioCodec = codec }],
            TranscodingProfiles =
            [
                new TranscodingProfile
                {
                    Type = DlnaProfileType.Audio,
                    Context = EncodingContext.Streaming,
                    Container = string.IsNullOrEmpty(container) ? "mp3" : container,
                    AudioCodec = codec ?? "mp3",
                    Protocol = MediaStreamProtocol.http
                }
            ],
            CodecProfiles = CodecProfiles(arguments, container ?? string.Empty, "audioSampleRate")
        };
    }

    // An HLS playlist asked by name: always a transcode to HLS, in the segment container and codec asked.
    private static DeviceProfile HlsProfile(Arguments arguments)
    {
        return new DeviceProfile
        {
            DirectPlayProfiles = [],
            TranscodingProfiles =
            [
                new TranscodingProfile
                {
                    Type = DlnaProfileType.Audio,
                    Context = EncodingContext.Streaming,
                    Container = arguments.Get<string>("segmentContainer") ?? "ts",
                    AudioCodec = arguments.Get<string>("audioCodec") ?? "aac",
                    Protocol = MediaStreamProtocol.hls,
                    MaxAudioChannels = arguments.Value<int>("maxAudioChannels")?.ToString(CultureInfo.InvariantCulture)
                }
            ],
            CodecProfiles = CodecProfiles(arguments, string.Empty, "audioSampleRate")
        };
    }

    private static CodecProfile[] CodecProfiles(Arguments arguments, string containers, string sampleRate)
    {
        var conditions = new (string Name, ProfileConditionValue Property)[]
            {
                (sampleRate, ProfileConditionValue.AudioSampleRate),
                ("maxAudioBitDepth", ProfileConditionValue.AudioBitDepth),
                ("maxAudioChannels", ProfileConditionValue.AudioChannels)
            }
            .Where(condition => arguments.Value<int>(condition.Name) is not null)
            .Select(condition => new ProfileCondition
            {
                Condition = ProfileConditionType.LessThanEqual,
                IsRequired = false,
                Property = condition.Property,
                Value = arguments.Value<int>(condition.Name)!.Value.ToString(CultureInfo.InvariantCulture)
            })
            .ToArray();
        return conditions.Length == 0 ? [] : [new CodecProfile { Type = CodecType.Audio, Container = containers, Conditions = conditions }];
    }

    private static string FolderOf(string path)
    {
        var plain = path.Split('?')[0];
        return plain[..(plain.LastIndexOf('/') + 1)];
    }

    // The variant playlist of the session, read through its master, for a client that asks main.m3u8 by itself.
    private async Task<IActionResult> VariantAsync(HttpContext http, PlaybackSession session, string prefix, string token)
    {
        var master = await _coordinator.ReadPlaylistAsync(session, session.UpstreamPath, http.RequestAborted).ConfigureAwait(false);
        if (!master.IsSuccess
            || HlsMaster.ReadVariants(master.Value!) is not [var variant, ..]
            || variant.Uri.StartsWith('/')
            || variant.Uri.Contains("://", StringComparison.Ordinal))
        {
            return new StatusCodeResult(StatusCodes.Status502BadGateway);
        }

        var path = session.UpstreamFolder() + variant.Uri;
        return await _relay.PlaylistAsync(http, session, path, FolderOf(path), prefix, token, StockPlaylistType, http.RequestAborted).ConfigureAwait(false);
    }

    private async Task<(PlaybackSession? Session, string Text)> FindOrOpenAsync(string fingerprint, PlaybackOpenRequest request, CancellationToken cancellationToken)
    {
        var gate = _locks[(int)((uint)StringComparer.Ordinal.GetHashCode(fingerprint) % LockCount)];
        if (!await gate.WaitAsync(_lockWait, cancellationToken).ConfigureAwait(false))
        {
            return (null, PlaybackRefusal.FailedText);
        }

        try
        {
            // After a fallback the session is under the copy that serves, one of the versions of the request.
            if (_opened.TryGetValue(fingerprint, out var id) && _sessions.Find(id) is { HasFailed: false } open && IsVersionOf(open.Identity, request))
            {
                return (open, PlaybackRefusal.Text);
            }

            foreach (var (key, sessionId) in _opened)
            {
                if (_sessions.Find(sessionId) is null)
                {
                    _opened.TryRemove(KeyValuePair.Create(key, sessionId));
                }
            }

            var outcome = await _coordinator.OpenAsync(request, cancellationToken).ConfigureAwait(false);
            if (outcome.Kind != PlaybackOutcomeKind.Remote || _sessions.Find(outcome.Response?.PlaySessionId) is not { } session)
            {
                // No local audio transcode: the end of the chain is a clear error.
                return (null, outcome.Kind == PlaybackOutcomeKind.LocalTranscode ? PlaybackRefusal.FailedText : PlaybackRefusal.TextOf(outcome));
            }

            _opened[fingerprint] = session.Id;
            return (session, PlaybackRefusal.Text);
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsVersionOf(PlaybackIdentity identity, PlaybackOpenRequest request)
        => identity == request.Identity
            || request.Fallbacks.Any(copy => identity == request.Identity with { ItemId = copy.ItemId, StubId = copy.Stub.Id });

    private sealed class Arguments
    {
        private readonly IDictionary<string, object?> _values;

        public Arguments(IDictionary<string, object?> values)
        {
            _values = values;
        }

        public T? Get<T>(string name)
            where T : class
        {
            return _values.TryGetValue(name, out var value) && value is T typed && !(typed is string { Length: 0 }) ? typed : null;
        }

        public T? Value<T>(string name)
            where T : struct
        {
            return _values.TryGetValue(name, out var value) && value is T typed ? typed : null;
        }
    }
}
