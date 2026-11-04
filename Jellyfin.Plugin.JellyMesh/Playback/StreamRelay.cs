using System;
using System.IO;
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
    /// <summary>
    /// Copies a stream.
    /// </summary>
    /// <param name="stream">The remote stream.</param>
    /// <param name="context">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public static async Task CopyAsync(RemoteContent stream, HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);

        var response = context.Response;
        var headers = stream.Headers;
        response.StatusCode = (int)stream.Status;
        response.ContentType = headers.ContentType;
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
            await stream.CopyToAsync(response.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client went away.
        }
        catch (IOException)
        {
            // Either side dropped. The headers are gone, so the only signal left is a broken connection.
            context.Abort();
        }
    }
}
