using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Security;
using MediaBrowser.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// The boundary of a peer on stock routes: what it may call, on which items, and how many streams at a time. A resource
/// filter, because it has to wrap the writing of the response: for a stream that is where the time goes, and the slot
/// stays held until the last byte. Anyone who is not a peer passes untouched.
/// </summary>
public sealed class PeerGuardFilter : IAsyncResourceFilter
{
    // The names stock binds: a query sent as one encoded key comes out of Jellyfin with keys that differ by case.
    private const string PlaySessionParameter = "playSessionId";
    private const string LegacyParameters = "params";
    private const string ConfirmedPlaySession = "JellyMesh.ConfirmedPlaySession";

    private static readonly string[] _itemRouteValues = ["itemId", "routeItemId"];

    private readonly PeerDirectory _peers;
    private readonly ProviderCatalog _catalog;
    private readonly StreamSlots _slots;
    private readonly AbandonedStreamCloser _closer;
    private readonly ConcurrencyLimit<Guid> _reads = new(ProviderProtocol.MaxReadsPerPeer);

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerGuardFilter"/> class.
    /// </summary>
    /// <param name="peers">The peers.</param>
    /// <param name="catalog">The catalog.</param>
    /// <param name="slots">The stream slots.</param>
    /// <param name="closer">What closes the transcodes of abandoned streams.</param>
    public PeerGuardFilter(PeerDirectory peers, ProviderCatalog catalog, StreamSlots slots, AbandonedStreamCloser closer)
    {
        _peers = peers;
        _catalog = catalog;
        _slots = slots;
        _closer = closer;
    }

    /// <summary>
    /// Builds the refusal of a stream over the limit: not an error of the server, so the consumer moves on to the next
    /// step of its chain.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The refusal.</returns>
    public static IActionResult RefuseOverLimit(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Headers[ProviderProtocol.RefusalHeader] = ProviderProtocol.StreamLimitRefusal;
        return new StatusCodeResult(StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    /// Notes the play session a <c>PlaybackInfo</c> confirmed, for when its answer turns out not to have left.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="playSessionId">The play session.</param>
    public static void NotePlaybackInfo(HttpContext context, string playSessionId)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Items[ConfirmedPlaySession] = playSessionId;
    }

    /// <inheritdoc />
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        if (await _peers.FindCallerAsync(http).ConfigureAwait(false) is not { } caller)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var action = context.ActionDescriptor as ControllerActionDescriptor;
        var kind = PeerRoutes.Classify(action, http.Request.Method);
        switch (kind)
        {
            case PeerRouteKind.Open when IsProviderRead(action, http.Request.Method):
                await ReadAsync(caller, context, next).ConfigureAwait(false);
                return;
            case PeerRouteKind.Open:
                await next().ConfigureAwait(false);
                return;
            case PeerRouteKind.Closed:
                context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
                return;
            case PeerRouteKind.StopEncoding when !_slots.Owns(caller.Grant.UserId, PlaySessionOf(http)):
                context.Result = RefusePlaySession();
                return;
            case PeerRouteKind.StopEncoding:
                await next().ConfigureAwait(false);
                return;
        }

        // The same answer as for an item that does not exist: a peer learns nothing of what is not shared with it.
        if (ItemIdOf(context.RouteData) is not { } itemId || _catalog.FindVisible(caller, itemId) is null)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status404NotFound);
            return;
        }

        if (kind == PeerRouteKind.Item)
        {
            await ReadAsync(caller, context, next).ConfigureAwait(false);
            return;
        }

        if (kind == PeerRouteKind.PlaybackInfo)
        {
            await PlaybackInfoAsync(caller, context, next).ConfigureAwait(false);
            return;
        }

        var playSession = StreamPlaySessionOf(http);
        var hasSession = !string.IsNullOrEmpty(playSession);

        // HLS without a play session would also kill every job of the device on this server.
        if (!hasSession && action?.ControllerName == "DynamicHls")
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return;
        }

        // A segment request kills the job of the play session it names when it restarts ffmpeg, as the stop route does.
        if (hasSession && !_slots.Owns(caller.Grant.UserId, playSession))
        {
            context.Result = RefusePlaySession();
            return;
        }

        using var request = _slots.TryEnter(caller.Grant.UserId, caller.Grant.MaxStreams, hasSession ? playSession : null, itemId, http.Abort);
        if (request is null)
        {
            context.Result = RefuseOverLimit(http);
            return;
        }

        // Before this stream starts, so that two ffmpeg of one peer never overlap on its limit.
        await _closer.CloseAbandonedAsync().ConfigureAwait(false);

        var executed = await next().ConfigureAwait(false);
        if (WasAbandoned(http, executed))
        {
            request.Abandon();
        }

        // Stock leaves the failed job behind with a request that never ends, and a retry would wait for it for good.
        if (hasSession && executed.Exception is FfmpegException)
        {
            await _closer.CloseAsync(playSession).ConfigureAwait(false);
        }
    }

    // One answer for all of them, the same for a play session that exists and one that does not. The joined values of a
    // repeated parameter and an empty one are never owned, so stock never gets to "every job of the device".
    private static StatusCodeResult RefusePlaySession() => new(StatusCodes.Status403Forbidden);

    // The close route binds no legacy params, so stock stops the play session of the query alone.
    private static string PlaySessionOf(HttpContext http) => http.Request.Query[PlaySessionParameter].ToString();

    // The play session stock acts on for a stream: field 20 of the first legacy params, else the query.
    private static string StreamPlaySessionOf(HttpContext http)
    {
        var parameters = http.Request.Query[LegacyParameters];
        return StubLookup.LegacyParamOf(parameters.Count > 0 ? parameters[0] : null, StubLookup.LegacyPlaySessionField) ?? PlaySessionOf(http);
    }

    private static bool Failed(ResourceExecutedContext executed)
    {
        return executed.HttpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError
            || (executed.Exception is not null && !executed.ExceptionHandled);
    }

    // A probe of the consumer drops its connection on purpose, after the first byte: the stream it proved is about to start.
    private static bool WasAbandoned(HttpContext http, ResourceExecutedContext executed)
    {
        return !http.Request.Headers.ContainsKey(ProviderProtocol.ProbeHeader)
            && (http.RequestAborted.IsCancellationRequested || Failed(executed));
    }

    private async Task PlaybackInfoAsync(PeerCaller caller, ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var executed = await next().ConfigureAwait(false);

        // The consumer never learned this play session, so nobody will ever release it.
        if ((http.RequestAborted.IsCancellationRequested || Failed(executed)) && http.Items[ConfirmedPlaySession] is string playSessionId)
        {
            _slots.Release(caller.Grant.UserId, playSessionId);
        }
    }

    private static bool IsProviderRead(ControllerActionDescriptor? action, string method)
    {
        return action?.ControllerTypeInfo == typeof(ProviderController) && HttpMethods.IsGet(method);
    }

    private async Task ReadAsync(PeerCaller caller, ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        // No refusal header: this is not the stream limit, and the consumer must not take it for that.
        using var read = _reads.TryEnter(caller.Grant.UserId);
        if (read is null)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status429TooManyRequests);
            return;
        }

        await next().ConfigureAwait(false);
    }

    private static Guid? ItemIdOf(RouteData route)
    {
        foreach (var name in _itemRouteValues)
        {
            if (route.Values.TryGetValue(name, out var value) && Guid.TryParse(value?.ToString(), out var id) && !id.Equals(Guid.Empty))
            {
                return id;
            }
        }

        return null;
    }
}
