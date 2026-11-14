using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Playback;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.JellyMesh.Segments;

/// <summary>
/// Answers no segments on <c>/MediaSegments/{itemId}</c> to a device whose play session serves a copy the segments do not
/// fit (Q255). Without such a session stock answers as it is.
/// </summary>
public sealed class SegmentRouteFilter : IAsyncActionFilter
{
    private readonly IAuthorizationContext _authorization;
    private readonly SegmentOffer _offer;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentRouteFilter"/> class.
    /// </summary>
    /// <param name="authorization">The authorization context.</param>
    /// <param name="offer">The segment offer.</param>
    public SegmentRouteFilter(IAuthorizationContext authorization, SegmentOffer offer)
    {
        _authorization = authorization;
        _offer = offer;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var executed = await next().ConfigureAwait(false);
        var (controller, action) = StubLookup.ActionOf(context);
        if (controller != "MediaSegments" || action != "GetItemSegments"
            || !context.ActionArguments.TryGetValue("itemId", out var value) || value is not Guid itemId
            || executed.Result is not ObjectResult { Value: QueryResult<MediaSegmentDto> { Items.Count: > 0 } } result)
        {
            return;
        }

        var authorization = await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false);
        if (authorization.User is { } user && !_offer.ServesDirect(user.Id, authorization.DeviceId, itemId))
        {
            result.Value = new QueryResult<MediaSegmentDto>();
        }
    }
}
