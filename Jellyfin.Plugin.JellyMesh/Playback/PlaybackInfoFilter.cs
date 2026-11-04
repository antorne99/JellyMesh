using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Answers <c>PlaybackInfo</c> for stubs, POST and GET. It is the only place that sees both the user and the device
/// profile. Runs after authentication, and redoes the check of the library: the controller it replaces would do it.
/// </summary>
public sealed class PlaybackInfoFilter : IAsyncActionFilter
{
    private const string BodyArgument = "playbackInfoDto";

    private readonly StubLookup _stubs;
    private readonly ILibraryManager _libraryManager;
    private readonly IAuthorizationContext _authorization;
    private readonly PlaybackCoordinator _coordinator;
    private readonly PlaybackRefusal _refusal;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackInfoFilter"/> class.
    /// </summary>
    /// <param name="stubs">The stubs.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="authorization">The authorization context.</param>
    /// <param name="coordinator">The coordinator.</param>
    /// <param name="refusal">The refusal.</param>
    public PlaybackInfoFilter(
        StubLookup stubs,
        ILibraryManager libraryManager,
        IAuthorizationContext authorization,
        PlaybackCoordinator coordinator,
        PlaybackRefusal refusal)
    {
        _stubs = stubs;
        _libraryManager = libraryManager;
        _authorization = authorization;
        _coordinator = coordinator;
        _refusal = refusal;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var (controller, action) = StubLookup.ActionOf(context);
        var itemId = StubLookup.ItemIdOf(context);
        var stub = controller == "MediaInfo" && action.Contains("PlaybackInfo", StringComparison.Ordinal) && itemId is { } id ? _stubs.Find(id) : null;
        if (stub is null)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var http = context.HttpContext;
        var authorization = await _authorization.GetAuthorizationInfo(http).ConfigureAwait(false);
        if (authorization.User is not { } user || string.IsNullOrEmpty(authorization.Token))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status401Unauthorized);
            return;
        }

        if (_libraryManager.GetItemById<BaseItem>(itemId!.Value, user) is null)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status404NotFound);
            return;
        }

        var deviceId = authorization.DeviceId ?? string.Empty;
        var arguments = new ArgumentReader(context.ActionArguments);
        var request = new PlaybackOpenRequest
        {
            Identity = new PlaybackIdentity(user.Id, deviceId, itemId.Value, stub.Id),
            Stub = stub,
            LocalToken = authorization.Token,
            DeviceProfile = arguments.Body<DeviceProfile>("DeviceProfile"),
            AudioStreamIndex = arguments.Value<int>("audioStreamIndex"),
            SubtitleStreamIndex = arguments.Value<int>("subtitleStreamIndex"),
            StartTimeTicks = arguments.Value<long>("startTimeTicks"),
            MaxStreamingBitrate = arguments.Value<int>("maxStreamingBitrate"),
            MaxAudioChannels = arguments.Value<int>("maxAudioChannels"),
            EnableDirectPlay = arguments.Value<bool>("enableDirectPlay"),
            EnableDirectStream = arguments.Value<bool>("enableDirectStream"),
            EnableTranscoding = arguments.Value<bool>("enableTranscoding"),
            AllowVideoStreamCopy = arguments.Value<bool>("allowVideoStreamCopy"),
            AllowAudioStreamCopy = arguments.Value<bool>("allowAudioStreamCopy"),
            AlwaysBurnInSubtitleWhenTranscoding = arguments.Value<bool>("alwaysBurnInSubtitleWhenTranscoding")
        };

        var outcome = await _coordinator.OpenAsync(request, http.RequestAborted).ConfigureAwait(false);
        switch (outcome.Kind)
        {
            case PlaybackOutcomeKind.Remote:
                context.Result = new OkObjectResult(outcome.Response);
                break;
            case PlaybackOutcomeKind.LocalTranscode when request.DeviceProfile is not null:
                await RunLocalTranscodeAsync(context, next, request, outcome).ConfigureAwait(false);
                break;
            default:
                context.Result = _refusal.Refuse(http, deviceId, outcome.AtStreamLimit ? PlaybackRefusal.LimitText : PlaybackRefusal.Text);
                break;
        }
    }

    private async Task RunLocalTranscodeAsync(ActionExecutingContext context, ActionExecutionDelegate next, PlaybackOpenRequest request, PlaybackOutcome outcome)
    {
        // The stock action builds the local TranscodingUrl. ffmpeg then reads the URL of the .strm, from loopback.
        var noCopy = new Dictionary<string, object?>
        {
            ["enableDirectPlay"] = false,
            ["enableDirectStream"] = false,
            ["enableTranscoding"] = true,
            ["allowVideoStreamCopy"] = false,
            ["allowAudioStreamCopy"] = false
        };
        foreach (var (name, value) in noCopy)
        {
            context.ActionArguments[name] = value;
        }

        var executed = await next().ConfigureAwait(false);
        if (executed.Result is not ObjectResult { Value: PlaybackInfoResponse response } || !CleanLocalResponse(response, request, outcome))
        {
            executed.Exception = null;
            executed.ExceptionHandled = true;
            executed.Result = _refusal.Refuse(context.HttpContext, request.Identity.DeviceId);
        }
    }

    private bool CleanLocalResponse(PlaybackInfoResponse response, PlaybackOpenRequest request, PlaybackOutcome outcome)
    {
        PlaybackSession? subtitles = null;
        foreach (var source in response.MediaSources)
        {
            if (string.IsNullOrEmpty(source.TranscodingUrl))
            {
                return false;
            }

            source.Path = null;
            source.SupportsDirectPlay = false;
            source.SupportsDirectStream = false;
            source.SupportsTranscoding = true;
            source.IsRemote = false;
            source.TranscodingUrl = PlaybackUrls.WithoutClientRetry(source.TranscodingUrl);
            foreach (var stream in source.MediaStreams ?? [])
            {
                stream.Path = null;
                if (stream.DeliveryUrl is not null && outcome.FileSource is { } file)
                {
                    subtitles = _coordinator.MapLocalSubtitle(request.Identity, file, stream, subtitles, request.LocalToken);
                }
            }
        }

        return response.MediaSources.Count > 0;
    }

    private sealed class ArgumentReader
    {
        private readonly IDictionary<string, object?> _arguments;
        private readonly object? _body;

        public ArgumentReader(IDictionary<string, object?> arguments)
        {
            _arguments = arguments;
            arguments.TryGetValue(BodyArgument, out _body);
        }

        // PlaybackInfoDto lives in Jellyfin.Api, which is not a NuGet package.
        public T? Body<T>(string name)
            where T : class
        {
            return _body?.GetType().GetProperty(name)?.GetValue(_body) as T;
        }

        // A query value wins over the body, as in the stock action.
        public T? Value<T>(string name)
            where T : struct
        {
            if (_arguments.TryGetValue(name, out var query) && query is T value)
            {
                return value;
            }

            var property = _body?.GetType().GetProperties().FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return property?.GetValue(_body) is T fromBody ? fromBody : null;
        }
    }
}
