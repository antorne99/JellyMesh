using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Tests.Remote;

/// <summary>
/// A server on a real socket, writing whatever a test wants on the wire: the handlers, timeouts and limits the plugin
/// configures only exist on this path, and a stub of the transport would never reach them.
/// </summary>
internal sealed class RawHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<string, NetworkStream, CancellationToken, Task> _answer;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _requests = [];
    private readonly Lock _lock = new();
    private long _bytesWritten;

    public RawHttpServer(Func<string, NetworkStream, CancellationToken, Task> answer)
    {
        _answer = answer;
        _listener.Start();
        _ = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public Uri BaseUrl => new("http://127.0.0.1:" + Port + "/");

    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_lock)
            {
                return _requests.ToArray();
            }
        }
    }

    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>Writes a chunk of a chunked body, counting it.</summary>
    public async Task WriteChunkAsync(Stream stream, byte[] chunk, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(chunk.Length.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "\r\n"), cancellationToken);
        await stream.WriteAsync(chunk, cancellationToken);
        await stream.WriteAsync("\r\n"u8.ToArray(), cancellationToken);
        Interlocked.Add(ref _bytesWritten, chunk.Length);
    }

    public static Task WriteAsync(Stream stream, string text, CancellationToken cancellationToken)
        => stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).AsTask();

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Dispose();
        _stop.Dispose();
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var head = new StringBuilder();
        var buffer = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(buffer, cancellationToken) == 0)
            {
                return null;
            }

            head.Append((char)buffer[0]);
        }

        return head.ToString();
    }

    private async Task AcceptAsync()
    {
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                _ = ServeAsync(client, token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped.
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream, cancellationToken);
                if (head is null)
                {
                    return;
                }

                lock (_lock)
                {
                    _requests.Add(head);
                }

                await _answer(head, stream, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // The client left, which is what most of these tests are about.
            }
        }
    }
}
