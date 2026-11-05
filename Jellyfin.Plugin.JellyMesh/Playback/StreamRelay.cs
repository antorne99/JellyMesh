using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Copies a remote stream to a client. Only status, the four headers of <see cref="RemoteContentHeaders"/> and the bytes
/// go through.
/// </summary>
public static class StreamRelay
{
    private const string AnyBytes = "application/octet-stream";

    // What a media route of this server may call its answer. Any other type goes as bytes: it comes from the remote, and
    // text/html under this origin would be a page of this server.
    private static readonly string[] _mediaPrefixes = ["video/", "audio/"];

    private static readonly HashSet<string> _mediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        AnyBytes,
        "application/json",
        "application/vnd.apple.mpegurl",
        "application/x-mpegurl",
        "application/x-subrip",
        "text/plain",
        "text/vtt",
        "text/x-ssa"
    };

    /// <summary>
    /// Copies a stream.
    /// </summary>
    /// <param name="stream">The remote stream.</param>
    /// <param name="context">The request.</param>
    /// <param name="stallLimit">The longest the remote may send nothing.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public static async Task CopyAsync(RemoteContent stream, HttpContext context, TimeSpan stallLimit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);

        var response = context.Response;
        var headers = stream.Headers;
        response.StatusCode = (int)stream.Status;
        response.ContentType = MediaTypeOf(headers.ContentType);
        response.ContentLength = headers.ContentLength;
        if (headers.ContentRange is { } range)
        {
            response.Headers.ContentRange = range;
        }

        if (headers.AcceptRanges is { } accept)
        {
            response.Headers.AcceptRanges = accept;
        }

        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        try
        {
            await stream.CopyToAsync(response.Body, stallLimit, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client went away.
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            // Either side dropped, or the remote went silent. The headers are gone, so the only signal left is a broken
            // connection.
            context.Abort();
        }
    }

    private static string? MediaTypeOf(string? contentType)
    {
        if (contentType is null)
        {
            return null;
        }

        var type = contentType.Split(';', 2)[0].Trim();
        return _mediaTypes.Contains(type) || _mediaPrefixes.Any(prefix => type.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ? contentType
            : AnyBytes;
    }
}
