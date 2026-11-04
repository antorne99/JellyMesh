using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// An open stream from a remote. Only the headers a client needs are kept: the rest of the answer stays here.
/// </summary>
public sealed class RemoteContent : IDisposable
{
    private readonly HttpResponseMessage? _response;
    private readonly Stream? _body;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteContent"/> class.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <param name="headers">The headers to pass on.</param>
    /// <param name="body">The body.</param>
    /// <param name="response">The response that owns the body.</param>
    public RemoteContent(HttpStatusCode status, RemoteContentHeaders headers, Stream? body, HttpResponseMessage? response = null)
    {
        Status = status;
        Headers = headers;
        _body = body;
        _response = response;
    }

    /// <summary>Gets the status.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>Gets the headers to pass on.</summary>
    public RemoteContentHeaders Headers { get; }

    /// <summary>
    /// Copies the body.
    /// </summary>
    /// <param name="target">The target.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The copy.</returns>
    public Task CopyToAsync(Stream target, CancellationToken cancellationToken)
    {
        return _body is null ? Task.CompletedTask : _body.CopyToAsync(target, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _body?.Dispose();
        _response?.Dispose();
    }
}
