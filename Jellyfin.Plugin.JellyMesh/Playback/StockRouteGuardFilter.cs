using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        "VideoAttachments"
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
        }
        else if ((_mediaControllers.Contains(controller) || IsFileAction(controller, action))
            && StubLookup.ItemIdOf(context) is { } itemId
            && _stubs.Find(itemId) is { } stub)
        {
            var authorization = await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false);
            if (authorization.User is not { } user)
            {
                context.Result = new StatusCodeResult(StatusCodes.Status401Unauthorized);
                return;
            }

            // A segment that restarts ffmpeg stops the job of the play session it names, as the stop route does.
            if (_libraryManager.GetItemById<BaseItem>(itemId, user) is null || _sessions.IsLocalOfAnother(user.Id, PlaySessionOf(context)))
            {
                context.Result = new StatusCodeResult(StatusCodes.Status404NotFound);
                return;
            }

            var executed = await next().ConfigureAwait(false);
            if (executed.Exception is not null && !executed.ExceptionHandled && _coordinator.TakeFileRefusal(stub.Id))
            {
                executed.Exception = null;
                executed.ExceptionHandled = true;
                executed.Result = _refusal.Refuse(context.HttpContext, authorization.DeviceId ?? string.Empty, PlaybackRefusal.LimitText);

                // Stock leaves the failed job behind with a request that never ends, and a retry would wait for it for good.
                if (!string.IsNullOrWhiteSpace(PlaySessionOf(context)))
                {
                    await _transcodes.KillTranscodingJobs(authorization.DeviceId ?? string.Empty, PlaySessionOf(context), _ => true).ConfigureAwait(false);
                }
            }

            return;
        }

        await next().ConfigureAwait(false);
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

    private static string? PlaySessionOf(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue("playbackStopInfo", out var report))
        {
            return (report as PlaybackStopInfo)?.PlaySessionId;
        }

        return context.ActionArguments.TryGetValue("playSessionId", out var value) ? value as string : null;
    }
}
