using System;
using System.Threading.Tasks;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// Takes a slot for a <c>PlaybackInfo</c> of a peer. This is where a stream starts, and refusing here is what sends the
/// consumer to its next source before anything is transcoded. An action filter, because the play session it needs is in
/// the result, and the slot must carry that name before the response leaves: the next request of the consumer uses it.
/// </summary>
public sealed class PeerPlaybackInfoFilter : IAsyncActionFilter
{
    private readonly PeerDirectory _peers;
    private readonly StreamSlots _slots;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerPlaybackInfoFilter"/> class.
    /// </summary>
    /// <param name="peers">The peers.</param>
    /// <param name="slots">The stream slots.</param>
    public PeerPlaybackInfoFilter(PeerDirectory peers, StreamSlots slots)
    {
        _peers = peers;
        _slots = slots;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        var isPlaybackInfo = PeerRoutes.Classify(context.ActionDescriptor as ControllerActionDescriptor, http.Request.Method) == PeerRouteKind.PlaybackInfo;
        if (!isPlaybackInfo || await _peers.FindCallerAsync(http).ConfigureAwait(false) is not { } caller)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var peerId = caller.Grant.UserId;
        var replaces = http.Request.Headers[ProviderProtocol.ReplacesHeader].ToString();
        var reservation = _slots.TryReserve(peerId, caller.Grant.MaxStreams, string.IsNullOrEmpty(replaces) ? null : replaces);
        if (reservation is null)
        {
            context.Result = PeerGuardFilter.RefuseOverLimit(http);
            return;
        }

        var confirmed = false;
        try
        {
            var executed = await next().ConfigureAwait(false);
            if (executed.Result is ObjectResult { Value: PlaybackInfoResponse { ErrorCode: null, PlaySessionId: { } playSessionId } })
            {
                _slots.Confirm(peerId, reservation, playSessionId);
                confirmed = true;
            }
        }
        finally
        {
            if (!confirmed)
            {
                _slots.Release(peerId, reservation);
            }
        }
    }
}
