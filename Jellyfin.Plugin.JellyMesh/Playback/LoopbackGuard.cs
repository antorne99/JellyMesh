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
    private static readonly string[] _forwardingHeaders = ["Forwarded", "X-Real-IP"];

    // X-Original-* is what the forwarded headers middleware leaves after consuming X-Forwarded-*.
    private static readonly string[] _forwardingPrefixes = ["X-Forwarded-", "X-Original-"];

    /// <summary>
    /// Judges a request by its socket. Forwarded headers never allow anything, and any of them refuses: the server never
    /// sends one, while a reverse proxy on the same host has a loopback socket and is relaying an unknown client.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>Whether it comes from the server itself.</returns>
    public static bool IsLocalRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Headers.Keys.Any(IsForwardingHeader))
        {
            return false;
        }

        // The connection address is rewritten by the forwarded headers middleware, the socket is not.
        // A socket without an IP endpoint, such as a Unix socket, is refused rather than judged by that address.
        if (context.Features.Get<IConnectionSocketFeature>()?.Socket is { } socket)
        {
            return socket.RemoteEndPoint is IPEndPoint endpoint && IsLoopback(endpoint.Address);
        }

        return IsLoopback(context.Connection.RemoteIpAddress);
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

    // Tells whether a header name is one that a proxy adds or that the forwarded headers middleware leaves.
    private static bool IsForwardingHeader(string name)
        => _forwardingHeaders.Any(header => header.Equals(name, StringComparison.OrdinalIgnoreCase))
            || _forwardingPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
