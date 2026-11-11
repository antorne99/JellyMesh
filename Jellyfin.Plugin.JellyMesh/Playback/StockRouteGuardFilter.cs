using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Covers the stock media routes for stubs. Several of them are anonymous in Jellyfin, and for a stub the server itself
/// reads the <c>.strm</c> from loopback, so without this anyone with an item ID would read remote data. Also learns of a
/// stop from <c>DELETE /Videos/ActiveEncodings</c>, which is how jellyfin-web ends a transcode. Stock stops a play session
/// for anyone who names it, so a stub played on this server is kept out of the reach of other users here.
/// </summary>
public sealed class StockRouteGuardFilter : IAsyncActionFilter
{
    private const string StopEncodingAction = "StopEncodingProcess";

    private static readonly HashSet<string> _mediaControllers = new(StringComparer.Ordinal)
    {
        "Videos",
        "DynamicHls",
        "Audio",
        "UniversalAudio",
        "Subtitle",
        "VideoAttachments",
        "Trickplay"
    };

    private readonly StubLookup _stubs;
    private readonly ILibraryManager _libraryManager;
    private readonly IAuthorizationContext _authorization;
    private readonly PlaybackCoordinator _coordinator;
    private readonly PlaybackSessionStore _sessions;
    private readonly PlaybackRefusal _refusal;
    private readonly ITranscodeManager _transcodes;

    /// <summary>
    /// Initializes a new instance of the <see cref="StockRouteGuardFilter"/> class.
    /// </summary>
    /// <param name="stubs">The stubs.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="authorization">The authorization context.</param>
    /// <param name="coordinator">The coordinator.</param>
    /// <param name="sessions">The sessions.</param>
    /// <param name="refusal">The refusal.</param>
    /// <param name="transcodes">The transcode manager.</param>
    public StockRouteGuardFilter(
        StubLookup stubs,
        ILibraryManager libraryManager,
        IAuthorizationContext authorization,
        PlaybackCoordinator coordinator,
        PlaybackSessionStore sessions,
        PlaybackRefusal refusal,
        ITranscodeManager transcodes)
    {
        _stubs = stubs;
        _libraryManager = libraryManager;
        _authorization = authorization;
        _coordinator = coordinator;
        _sessions = sessions;
        _refusal = refusal;
        _transcodes = transcodes;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var (controller, action) = StubLookup.ActionOf(context);
        if (controller == "DynamicHls" && string.IsNullOrWhiteSpace(PlaySessionOf(context)) && DeviceOf(context) is { } device)
        {
            // A segment without a play session makes stock stop every job of its device, whoever asks.
            var caller = (await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false)).User?.Id ?? Guid.Empty;
            if (_sessions.IsLocalDeviceOfAnother(caller, device))
            {
                context.Result = new StatusCodeResult(StatusCodes.Status404NotFound);
                return;
            }
        }

        if (IsStop(controller, action))
        {
            var caller = (await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false)).User?.Id;
            var playSessionId = PlaySessionOf(context);
            if (caller is { } user && _sessions.IsLocalOfAnother(user, playSessionId))
            {
                // What stock answers for a play session it does not know.
                context.Result = new NoContentResult();
                return;
            }

            if (action == StopEncodingAction && caller is { } owner && _sessions.Find(playSessionId) is { } session && session.Identity.UserId == owner)
            {
                await _coordinator.CloseAsync(session.Id).ConfigureAwait(false);
            }

            await next().ConfigureAwait(false);
            if (caller is { } stopper)
            {
                _sessions.EndLocal(stopper, playSessionId);
            }

            return;
        }
        else if (IsReport(controller, action) && _sessions.LocalItemOf(PlaySessionOf(context)) is not null)
        {
            if ((await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false)).User is { } reporter)
            {
                // A report of another user would keep the stock job of the owner alive.
                if (_sessions.IsLocalOfAnother(reporter.Id, PlaySessionOf(context)))
                {
                    context.Result = new NoContentResult();
                    return;
                }

                _sessions.UseLocal(reporter.Id, PlaySessionOf(context));
            }
        }
        else if ((_mediaControllers.Contains(controller) || IsFileAction(controller, action))
            && StubsOf(context, out var recorded) is var stubs
            && (stubs.Count > 0 || recorded))
        {
            var authorization = await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false);
            if (authorization.User is not { } user)
            {
                context.Result = new StatusCodeResult(StatusCodes.Status401Unauthorized);
                return;
            }

            // A segment that restarts ffmpeg stops the job of the play session it names, as the stop route does.
            if (stubs.Any(named => _libraryManager.GetItemById<BaseItem>(named.Key, user) is null) || _sessions.IsLocalOfAnother(user.Id, PlaySessionOf(context)))
            {
                context.Result = new StatusCodeResult(StatusCodes.Status404NotFound);
                return;
            }

            _sessions.UseLocal(user.Id, PlaySessionOf(context));
            var executed = await next().ConfigureAwait(false);

            // Stock leaves the failed job behind with a request that never ends, and a retry would wait for it for good.
            if (executed.Exception is FfmpegException && !string.IsNullOrWhiteSpace(PlaySessionOf(context)))
            {
                await _transcodes.KillTranscodingJobs(authorization.DeviceId ?? string.Empty, PlaySessionOf(context), _ => true).ConfigureAwait(false);
            }

            if (executed.Exception is not null && !executed.ExceptionHandled && stubs.Values.Any(stub => _coordinator.TakeFileRefusal(stub.Id)))
            {
                executed.Exception = null;
                executed.ExceptionHandled = true;
                executed.Result = _refusal.Refuse(context.HttpContext, authorization.DeviceId ?? string.Empty, PlaybackRefusal.LimitText);
            }

            return;
        }

        await next().ConfigureAwait(false);
    }

    // Finds every stub the action names: as the entity, as the version, or through the local play session of a stub. The
    // play session counts even when its stub no longer resolves.
    private Dictionary<Guid, StubEntry> StubsOf(ActionExecutingContext context, out bool recorded)
    {
        var stubs = new Dictionary<Guid, StubEntry>();
        var named = StubLookup.NamedItemIdsOf(context).ToList();
        var played = _sessions.LocalItemOf(PlaySessionOf(context));
        recorded = played is not null;
        if (played is { } item)
        {
            named.Add(item);
        }

        foreach (var id in named)
        {
            if (_stubs.Find(id) is { } stub)
            {
                stubs[id] = stub;
            }
        }

        return stubs;
    }

    private static bool IsFileAction(string controller, string action)
    {
        return controller == "Library" && action is "GetDownload" or "GetFile";
    }

    private static bool IsStop(string controller, string action)
    {
        return (controller == "HlsSegment" && action == StopEncodingAction)
            || (controller == "Playstate" && action is "ReportPlaybackStopped" or "OnPlaybackStopped" or "OnPlaybackStoppedLegacy");
    }

    // A start, a progress or a ping of its user keeps a local play session in use; one of another user gets 204.
    private static bool IsReport(string controller, string action)
    {
        return controller == "Playstate"
            && action is "ReportPlaybackStart" or "OnPlaybackStart" or "OnPlaybackStartLegacy"
                or "ReportPlaybackProgress" or "OnPlaybackProgress" or "OnPlaybackProgressLegacy" or "PingPlaybackSession";
    }

    private static string? DeviceOf(ActionExecutingContext context)
    {
        return StubLookup.LegacyParamOf(context, StubLookup.LegacyDeviceField)
            ?? (context.ActionArguments.TryGetValue("deviceId", out var value) ? value as string : null);
    }

    private static string? PlaySessionOf(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue("playbackStopInfo", out var report))
        {
            return (report as PlaybackStopInfo)?.PlaySessionId;
        }

        foreach (var name in new[] { "playbackProgressInfo", "playbackStartInfo" })
        {
            if (context.ActionArguments.TryGetValue(name, out var progress))
            {
                return (progress as PlaybackProgressInfo)?.PlaySessionId;
            }
        }

        return StubLookup.LegacyParamOf(context, StubLookup.LegacyPlaySessionField)
            ?? (context.ActionArguments.TryGetValue("playSessionId", out var value) ? value as string : null);
    }
}
