using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Playback;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
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
    private const string LiveStreamArgument = "liveStreamId";
    private const string BodyArgument = "playbackInfoDto";
    private const string LiveStreamProperty = "LiveStreamId";
    private const string PostedAction = "GetPostedPlaybackInfo";

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

        // Stock would answer with that live stream, found by its id alone, in place of the item.
        if (NamesLiveStream(context))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
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

            // A consumer that left never learns the play session: the slot would sit there under a name nobody knows.
            if (!http.RequestAborted.IsCancellationRequested
                && executed.Result is ObjectResult { Value: PlaybackInfoResponse { ErrorCode: null, PlaySessionId: { } playSessionId } response })
            {
                // Stock stops every job of a device for a segment with no play session, so the devices are the peer's too.
                var devices = PlaybackInfoFilter.DevicesOf(response, caller.DeviceId);
                _slots.Confirm(peerId, reservation, playSessionId, StubLookup.ItemIdOf(context) ?? Guid.Empty, devices);
                PeerGuardFilter.NotePlaybackInfo(http, playSessionId);
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

    // Stock takes the live stream from the query, else from the posted body, whose type is not in a NuGet package.
    // A posted body that is not bound, or has no string LiveStreamId, counts as naming one: a later Jellyfin renamed it.
    private static bool NamesLiveStream(ActionExecutingContext context)
    {
        var query = context.ActionArguments.TryGetValue(LiveStreamArgument, out var value) ? value as string : null;
        if (!string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        if (!string.Equals(context.ActionDescriptor is ControllerActionDescriptor action ? action.ActionName : null, PostedAction, StringComparison.Ordinal))
        {
            return false;
        }

        if (!context.ActionArguments.TryGetValue(BodyArgument, out var body))
        {
            return true;
        }

        if (body is null)
        {
            return false;
        }

        var property = body.GetType().GetProperty(LiveStreamProperty);
        return property?.PropertyType != typeof(string) || !string.IsNullOrWhiteSpace(property.GetValue(body) as string);
    }
}
