using System;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Tells whether a request comes from the server itself, the only caller of the <c>.strm</c> endpoint.
/// </summary>
public static class LoopbackGuard
{
    private static readonly string[] _forwardingHeaders = ["Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Original-For", "X-Real-IP"];

    /// <summary>
    /// Judges a request by its socket. Forwarded headers never allow anything, and any of them refuses: the server never
    /// sends one, while a reverse proxy on the same host has a loopback socket and is relaying an unknown client.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>Whether it comes from the server itself.</returns>
    public static bool IsLocalRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_forwardingHeaders.Any(context.Request.Headers.ContainsKey))
        {
            return false;
        }

        // The connection address is rewritten by the forwarded headers middleware, the socket is not.
        var socket = context.Features.Get<IConnectionSocketFeature>()?.Socket;
        var address = socket?.RemoteEndPoint is IPEndPoint endpoint ? endpoint.Address : context.Connection.RemoteIpAddress;
        return IsLoopback(address);
    }

    /// <summary>
    /// Tells whether an address is loopback, also when it is an IPv4 address mapped to IPv6.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>Whether it is loopback.</returns>
    public static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        return IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }
}
