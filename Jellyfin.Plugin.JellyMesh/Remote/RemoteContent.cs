using System;
using System.Buffers;
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
    private const int CopyBufferBytes = 80 * 1024;

    /// <summary>
    /// The shortest wait for a client to take the next bytes that counts as a pause. A player that plays waits far less for
    /// the bytes of one copy buffer, even at the lowest bitrates.
    /// </summary>
    public static readonly TimeSpan ClientPause = TimeSpan.FromSeconds(30);

    private readonly HttpResponseMessage? _response;
    private readonly Stream? _body;
    private IDisposable? _release;
    private CancellationToken _ended;
    private int _disposed;

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
    /// Ties something to the life of this stream, such as the place it holds among the streams of a server.
    /// </summary>
    /// <param name="release">Disposed with the stream.</param>
    /// <returns>This stream.</returns>
    public RemoteContent HoldUntilDisposed(IDisposable release)
    {
        _release = release;
        return this;
    }

    /// <summary>
    /// Ends the copy when a token fires: the server of the stream is being removed (413).
    /// </summary>
    /// <param name="ended">Fires when the stream has to end.</param>
    /// <returns>This stream.</returns>
    public RemoteContent EndsWith(CancellationToken ended)
    {
        _ended = ended;
        return this;
    }

    /// <summary>
    /// Copies the body with nothing held in between, and gives up on a remote that stops sending. Only the wait for the
    /// remote counts: a client that reads slowly or not at all leaves no read waiting.
    /// </summary>
    /// <param name="target">The target.</param>
    /// <param name="stallLimit">The longest wait for the remote.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="clock">The clock that times the client, the system clock if null.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="TimeoutException">The remote sent nothing for <paramref name="stallLimit"/>.</exception>
    /// <exception cref="RemoteStreamBrokenException">The connection to the remote broke.</exception>
    /// <exception cref="RemoteStreamClosedException">The token of <see cref="EndsWith"/> fired.</exception>
    public async Task CopyToAsync(Stream target, TimeSpan stallLimit, CancellationToken cancellationToken, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (_body is null)
        {
            return;
        }

        clock ??= TimeProvider.System;
        DateTimeOffset? pauseEnded = null;
        using var copy = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _ended);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(copy.Token);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            while (true)
            {
                stall.CancelAfter(stallLimit);
                int read;
                try
                {
                    read = await _body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!copy.IsCancellationRequested)
                {
                    throw new TimeoutException("The remote stopped sending.");
                }
                catch (IOException exception) when (!copy.IsCancellationRequested)
                {
                    throw new RemoteStreamBrokenException("The remote dropped the stream.", exception)
                    {
                        SinceClientPause = clock.GetUtcNow() - pauseEnded
                    };
                }

                stall.CancelAfter(Timeout.InfiniteTimeSpan);
                if (read == 0)
                {
                    return;
                }

                var started = clock.GetUtcNow();
                await target.WriteAsync(buffer.AsMemory(0, read), copy.Token).ConfigureAwait(false);
                var written = clock.GetUtcNow();
                if (written - started >= ClientPause)
                {
                    pauseEnded = written;
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException
            && _ended.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new RemoteStreamClosedException("The server of the stream is being removed.", exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _body?.Dispose();
            _response?.Dispose();
            _release?.Dispose();
        }
    }
}
