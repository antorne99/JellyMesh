using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Security;
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
    private const string PlaySessionParameter = "PlaySessionId";

    private static readonly string[] _itemRouteValues = ["itemId", "routeItemId"];

    private readonly PeerDirectory _peers;
    private readonly ProviderCatalog _catalog;
    private readonly StreamSlots _slots;
    private readonly ConcurrencyLimit<Guid> _reads = new(ProviderProtocol.MaxReadsPerPeer);

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerGuardFilter"/> class.
    /// </summary>
    /// <param name="peers">The peers.</param>
    /// <param name="catalog">The catalog.</param>
    /// <param name="slots">The stream slots.</param>
    public PeerGuardFilter(PeerDirectory peers, ProviderCatalog catalog, StreamSlots slots)
    {
        _peers = peers;
        _catalog = catalog;
        _slots = slots;
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

        if (kind != PeerRouteKind.Stream)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var playSession = PlaySessionOf(http);
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

        using var request = _slots.TryEnter(caller.Grant.UserId, caller.Grant.MaxStreams, hasSession ? playSession : null);
        if (request is null)
        {
            context.Result = RefuseOverLimit(http);
            return;
        }

        await next().ConfigureAwait(false);
    }

    // One answer for all of them, the same for a play session that exists and one that does not. The joined values of a
    // repeated parameter and an empty one are never owned, so stock never gets to "every job of the device".
    private static StatusCodeResult RefusePlaySession() => new(StatusCodes.Status403Forbidden);

    private static string PlaySessionOf(HttpContext http) => http.Request.Query[PlaySessionParameter].ToString();

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
