using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Answers <c>PlaybackInfo</c> for stubs and for titles with versions, POST and GET. It is the only place that sees both the
/// user and the device profile. Runs after authentication, and redoes the check of the library the stock action would do.
/// </summary>
public sealed class PlaybackInfoFilter : IAsyncActionFilter
{
    private const string BodyArgument = "playbackInfoDto";
    private const string ItemArgument = "itemId";
    private const string MediaSourceArgument = "mediaSourceId";

    private readonly StubLookup _stubs;
    private readonly IAuthorizationContext _authorization;
    private readonly PlaybackCoordinator _coordinator;
    private readonly PlaybackRefusal _refusal;
    private readonly LocalCopyLookup _localCopies;
    private readonly PlaybackVersions _versions;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackInfoFilter"/> class.
    /// </summary>
    /// <param name="stubs">The stubs.</param>
    /// <param name="authorization">The authorization context.</param>
    /// <param name="coordinator">The coordinator.</param>
    /// <param name="refusal">The refusal.</param>
    /// <param name="localCopies">The lookup of local copies.</param>
    /// <param name="versions">The versions of a title.</param>
    public PlaybackInfoFilter(
        StubLookup stubs,
        IAuthorizationContext authorization,
        PlaybackCoordinator coordinator,
        PlaybackRefusal refusal,
        LocalCopyLookup localCopies,
        PlaybackVersions versions)
    {
        _stubs = stubs;
        _authorization = authorization;
        _coordinator = coordinator;
        _refusal = refusal;
        _localCopies = localCopies;
        _versions = versions;
    }

    /// <summary>
    /// Finds the devices a play session runs on: the device of the request and the ones of the transcoding URLs, read as a
    /// segment request binds them. Stock writes the decoded header device into the URL unencoded, so the two can differ.
    /// </summary>
    /// <param name="response">The answer of <c>PlaybackInfo</c>.</param>
    /// <param name="deviceId">The device of the request, from its authorization header.</param>
    /// <returns>The devices, some of them null or repeated.</returns>
    public static IEnumerable<string?> DevicesOf(PlaybackInfoResponse response, string? deviceId)
    {
        ArgumentNullException.ThrowIfNull(response);

        yield return deviceId;
        foreach (var source in response.MediaSources ?? [])
        {
            if (source.TranscodingUrl?.Split('?', 2) is [_, var query]
                && QueryHelpers.ParseQuery(query).TryGetValue("DeviceId", out var devices))
            {
                yield return devices.FirstOrDefault();
            }
        }
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var (controller, action) = StubLookup.ActionOf(context);
        var routeId = StubLookup.ItemIdOf(context);
        if (controller != "MediaInfo" || !action.Contains("PlaybackInfo", StringComparison.Ordinal) || routeId is not { } route)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // R:versioni §5: the version comes from the query, else from the body, as the stock action merges them.
        var arguments = new ArgumentReader(context.ActionArguments);
        var asked = arguments.Version(MediaSourceArgument);
        var routeStub = _stubs.Find(route);
        if (routeStub is null && (asked is not { } named || named.Equals(route) || _stubs.Find(named) is null) && !_stubs.HasVersions(route))
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

        // A version the user cannot see, in no library (D17) or of another title, is not found, and reaches no remote.
        if (!_stubs.Sees(user, route)
            || (asked is { } version && (!_stubs.Sees(user, version) || !_versions.IsVersionOf(route, version))))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status404NotFound);
            return;
        }

        var target = asked ?? (routeStub is null ? route : _versions.Choose(route, routeStub, user));
        var targetStub = target.Equals(route) ? routeStub : _stubs.Find(target);
        if (targetStub is null && routeStub is null)
        {
            await PlayStockVersionAsync(context, next, target).ConfigureAwait(false);
            return;
        }

        var deviceId = authorization.DeviceId ?? string.Empty;
        var request = new PlaybackOpenRequest
        {
            Identity = new PlaybackIdentity(user.Id, deviceId, target, (targetStub ?? routeStub)!.Id),
            Stub = (targetStub ?? routeStub)!,
            Fallbacks = targetStub is null ? [] : _versions.Fallbacks(target, targetStub, user),
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

        // The local copy under a stub is stock's, through the file route of the local copy.
        if (targetStub is null)
        {
            await PlayLocalCopyAsync(context, next, request, target).ConfigureAwait(false);
            return;
        }

        var outcome = await _coordinator.OpenAsync(request, http.RequestAborted).ConfigureAwait(false);
        switch (outcome.Kind)
        {
            case PlaybackOutcomeKind.Remote:
                context.Result = new OkObjectResult(outcome.Response);
                break;
            case PlaybackOutcomeKind.LocalTranscode when request.DeviceProfile is not null:
                // After a fallback another version serves, and stock must transcode its .strm.
                var served = outcome.Served is { } identity ? request with { Identity = identity } : request;
                await RunLocalTranscodeAsync(context, next, served, outcome).ConfigureAwait(false);
                break;
            default:
                await RefuseOrPlayLocalCopyAsync(context, next, request, outcome, user).ConfigureAwait(false);
                break;
        }
    }

    // Keeps only the source of the version served: stock may list every version, and clients play the first one.
    private static void KeepOnly(PlaybackInfoResponse response, Guid versionId)
    {
        var id = versionId.ToString("N");
        var kept = response.MediaSources.Where(source => string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (kept.Count > 0)
        {
            response.MediaSources = kept;
        }
    }

    // Stock answers for a local version under a local item. The GET action has no version to pin, so the answer is trimmed.
    private static async Task PlayStockVersionAsync(ActionExecutingContext context, ActionExecutionDelegate next, Guid versionId)
    {
        context.ActionArguments[MediaSourceArgument] = versionId.ToString("N");
        var executed = await next().ConfigureAwait(false);
        if (executed.Result is ObjectResult { Value: PlaybackInfoResponse response })
        {
            KeepOnly(response, versionId);
        }
    }

    private static void HideFile(MediaSourceInfo source)
    {
        source.Path = null;
        source.SupportsDirectPlay = false;
        source.SupportsDirectStream = false;
        source.SupportsTranscoding = true;
        source.IsRemote = false;
    }

    private static bool CleanLocalCopyResponse(PlaybackInfoResponse response, PlaybackOpenRequest request, Guid localItemId)
    {
        if (response.ErrorCode is not null || response.MediaSources.Count == 0)
        {
            return false;
        }

        KeepOnly(response, localItemId);

        foreach (var source in response.MediaSources)
        {
            // What the client could play as it is goes by file, without a transcode nobody needs.
            if (source.SupportsDirectPlay || source.SupportsDirectStream || string.IsNullOrEmpty(source.TranscodingUrl))
            {
                var container = PlaybackResponseBuilder.ContainerOf(source);
                source.TranscodingUrl = PlaybackUrls.LocalFile(localItemId, source.Id, container, request.LocalToken);
                source.TranscodingSubProtocol = MediaStreamProtocol.http;
                source.TranscodingContainer = container;
            }

            HideFile(source);
            source.Protocol = MediaProtocol.Http;

            // The client reports this id as the version played, and the server keeps the watch state on that version.
            source.Id = localItemId.ToString("N");
            foreach (var stream in source.MediaStreams ?? [])
            {
                stream.Path = null;
            }
        }

        return true;
    }

    private async Task RefuseOrPlayLocalCopyAsync(ActionExecutingContext context, ActionExecutionDelegate next, PlaybackOpenRequest request, PlaybackOutcome outcome, User user)
    {
        // A local copy in no library is not one to fall back on for a non-admin (D17).
        if (_localCopies.Find(request.Stub, user) is not { } local || !_stubs.Sees(user, local.Id))
        {
            context.Result = _refusal.Refuse(context.HttpContext, request.Identity.DeviceId, PlaybackRefusal.TextOf(outcome));
            return;
        }

        await PlayLocalCopyAsync(context, next, request, local.Id).ConfigureAwait(false);
    }

    private async Task PlayLocalCopyAsync(ActionExecutingContext context, ActionExecutionDelegate next, PlaybackOpenRequest request, Guid localItemId)
    {
        // The stock action answers for the local item. The source the client asked for is one of the stub.
        context.ActionArguments[ItemArgument] = localItemId;
        context.ActionArguments[MediaSourceArgument] = localItemId.ToString("N");
        var executed = await next().ConfigureAwait(false);
        if (executed.Result is not ObjectResult { Value: PlaybackInfoResponse response } || !CleanLocalCopyResponse(response, request, localItemId))
        {
            executed.Exception = null;
            executed.ExceptionHandled = true;
            executed.Result = _refusal.Refuse(context.HttpContext, request.Identity.DeviceId, PlaybackRefusal.FailedText);
            return;
        }

        _coordinator.RecordLocalPlaySession(request.Identity.UserId, localItemId, response.PlaySessionId, DevicesOf(response, request.Identity.DeviceId));
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

        // Stock transcodes the .strm of the version served, which may not be the item of the route.
        context.ActionArguments[ItemArgument] = request.Identity.ItemId;
        context.ActionArguments[MediaSourceArgument] = request.Identity.ItemId.ToString("N");
        var executed = await next().ConfigureAwait(false);
        if (executed.Result is ObjectResult { Value: PlaybackInfoResponse trimmed })
        {
            KeepOnly(trimmed, request.Identity.ItemId);
        }

        if (executed.Result is not ObjectResult { Value: PlaybackInfoResponse response } || !CleanLocalResponse(response, request, outcome))
        {
            executed.Exception = null;
            executed.ExceptionHandled = true;
            executed.Result = _refusal.Refuse(context.HttpContext, request.Identity.DeviceId, PlaybackRefusal.FailedText);
            return;
        }

        _coordinator.RecordLocalPlaySession(request.Identity.UserId, request.Identity.ItemId, response.PlaySessionId, DevicesOf(response, request.Identity.DeviceId));
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

            HideFile(source);
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

        // Reads the version a client names, as a Guid or as text, the query over the body. Anything else counts as none.
        public Guid? Version(string name)
        {
            var value = _arguments.TryGetValue(name, out var query) && query is not null && !(query is string { Length: 0 })
                ? query
                : _body?.GetType().GetProperties().FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.GetValue(_body);
            return (value is Guid id || (value is string text && Guid.TryParse(text, out id))) && !id.Equals(Guid.Empty) ? id : null;
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
