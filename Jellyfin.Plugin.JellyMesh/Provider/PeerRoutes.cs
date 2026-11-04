using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// What a peer may call on stock routes. It is a list of what is allowed, so that a route added by a future Jellyfin stays
/// closed: a peer is a server with a login, and the stock API would otherwise show it every library and every stub.
/// </summary>
public static class PeerRoutes
{
    /// <summary>
    /// Sorts a request of a peer.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <param name="method">The HTTP method.</param>
    /// <returns>How to treat it.</returns>
    public static PeerRouteKind Classify(ControllerActionDescriptor? action, string method)
    {
        if (action is null)
        {
            return PeerRouteKind.Closed;
        }

        if (action.ControllerTypeInfo.Namespace == typeof(ProviderProtocol).Namespace)
        {
            return PeerRouteKind.Open;
        }

        var reads = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        return (action.ControllerName, action.ActionName) switch
        {
            ("User", "GetCurrentUser" or "AuthenticateUserByName") => PeerRouteKind.Open,
            ("System", "GetPublicSystemInfo") => PeerRouteKind.Open,
            ("MediaInfo", var name) when name.Contains("PlaybackInfo", StringComparison.Ordinal) => PeerRouteKind.PlaybackInfo,
            ("Videos", var name) when reads && name.StartsWith("GetVideoStream", StringComparison.Ordinal) => PeerRouteKind.Stream,
            ("DynamicHls", _) when reads => PeerRouteKind.Stream,
            ("Image" or "Subtitle", _) when reads => PeerRouteKind.Item,
            _ => PeerRouteKind.Closed
        };
    }
}
