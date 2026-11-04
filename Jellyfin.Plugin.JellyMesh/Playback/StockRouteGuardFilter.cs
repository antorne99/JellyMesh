using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Covers the stock media routes for stubs. Several of them are anonymous in Jellyfin, and for a stub the server itself
/// reads the <c>.strm</c> from loopback, so without this anyone with an item ID would read remote data. Also learns of a
/// stop from <c>DELETE /Videos/ActiveEncodings</c>, which is how jellyfin-web ends a transcode.
/// </summary>
public sealed class StockRouteGuardFilter : IAsyncActionFilter
{
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

    /// <summary>
    /// Initializes a new instance of the <see cref="StockRouteGuardFilter"/> class.
    /// </summary>
    /// <param name="stubs">The stubs.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="authorization">The authorization context.</param>
    /// <param name="coordinator">The coordinator.</param>
    /// <param name="sessions">The sessions.</param>
    public StockRouteGuardFilter(
        StubLookup stubs,
        ILibraryManager libraryManager,
        IAuthorizationContext authorization,
        PlaybackCoordinator coordinator,
        PlaybackSessionStore sessions)
    {
        _stubs = stubs;
        _libraryManager = libraryManager;
        _authorization = authorization;
        _coordinator = coordinator;
        _sessions = sessions;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var (controller, action) = StubLookup.ActionOf(context);
        if (controller == "HlsSegment" && action == "StopEncodingProcess")
        {
            await CloseSessionAsync(context).ConfigureAwait(false);
        }
        else if ((_mediaControllers.Contains(controller) || IsFileAction(controller, action))
            && StubLookup.ItemIdOf(context) is { } itemId
            && _stubs.Find(itemId) is not null)
        {
            var authorization = await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false);
            if (authorization.User is not { } user)
            {
                context.Result = new StatusCodeResult(StatusCodes.Status401Unauthorized);
                return;
            }

            if (_libraryManager.GetItemById<BaseItem>(itemId, user) is null)
            {
                context.Result = new StatusCodeResult(StatusCodes.Status404NotFound);
                return;
            }
        }

        await next().ConfigureAwait(false);
    }

    private static bool IsFileAction(string controller, string action)
    {
        return controller == "Library" && action is "GetDownload" or "GetFile";
    }

    private async Task CloseSessionAsync(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue("playSessionId", out var value)
            && _sessions.Find(value as string) is { } session)
        {
            var authorization = await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false);
            if (authorization.User?.Id == session.Identity.UserId)
            {
                await _coordinator.CloseAsync(session.Id).ConfigureAwait(false);
            }
        }
    }
}
