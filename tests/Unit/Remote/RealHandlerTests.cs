using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Remote;

/// <summary>
/// The two clients as the plugin registers them, against a real socket.
/// </summary>
public sealed class RealHandlerTests : IDisposable
{
    private const string Ok = "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}";
    private const string Chunked = "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n";
    private const string ChunkedImage = "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: image/jpeg\r\nTransfer-Encoding: chunked\r\n\r\n";

    private static readonly RemoteSession _session = new(TestValues.Token, "abc");

    private readonly ServiceProvider _services;
    private readonly IHttpClientFactory _factory;

    public RealHandlerTests()
    {
        var services = new ServiceCollection();
        services.AddRemoteHttpClients();
        _services = services.BuildServiceProvider();
        _factory = _services.GetRequiredService<IHttpClientFactory>();
    }

    public void Dispose() => _services.Dispose();

    private static RemoteEndpoint EndpointOf(RawHttpServer server) => new(server.BaseUrl, "device");

    private static Task<T> WithinAsync<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(20));

    private JellyfinRemoteClient Catalog(TimeSpan? limit = null)
    {
        return new JellyfinRemoteClient(_factory) { RequestLimit = limit ?? JellyfinRemoteClient.RequestTimeout, Delay = (_, _) => Task.CompletedTask };
    }

    private RemotePlaybackClient Playback() => new(_factory);

    [Theory]
    [InlineData(JellyfinRemoteClient.HttpClientName)]
    [InlineData(RemotePlaybackClient.HttpClientName)]
    public void No_client_of_the_plugin_follows_a_redirect_by_itself(string name)
    {
        var factory = _services.GetRequiredService<IHttpMessageHandlerFactory>();
        HttpMessageHandler? handler = factory.CreateHandler(name);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler;
        }

        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.False(sockets.UseCookies);
        Assert.False(sockets.UseProxy);
        Assert.Equal(JellyfinRemoteClient.ConnectTimeout, sockets.ConnectTimeout);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task A_redirect_to_another_host_reaches_nobody_with_any_call(int status)
    {
        using var other = new RawHttpServer((_, stream, token) => RawHttpServer.WriteAsync(stream, Ok, token));
        var redirect = "HTTP/1.1 " + status + " Moved\r\nConnection: close\r\nContent-Length: 0\r\nLocation: http://127.0.0.1:" + other.Port + "/stolen\r\n\r\n";
        using var hostile = new RawHttpServer((_, stream, token) => RawHttpServer.WriteAsync(stream, redirect, token));
        var endpoint = EndpointOf(hostile);
        var catalog = Catalog();
        var playback = Playback();

        var results = new[]
        {
            (await catalog.AuthenticateAsync(endpoint, TestValues.Credentials, CancellationToken.None)).Failure,
            (await catalog.GetLibrariesAsync(endpoint, _session, CancellationToken.None)).Failure,
            (await catalog.GetImageAsync(endpoint, _session, "item", "Primary", 0, CancellationToken.None)).Failure,
            (await catalog.GetItemsAsync(endpoint, _session, new RemoteItemQuery("lib", RemoteItemType.Movie, 0, 1), CancellationToken.None)).Failure,
            (await playback.GetPlaybackInfoAsync(endpoint, _session, new RemotePlaybackRequest { ItemId = "i", MediaSourceId = "m" }, CancellationToken.None)).Failure,
            (await playback.ReadPlaylistAsync(endpoint, _session, "/x.m3u8", CancellationToken.None)).Failure,
            (await playback.OpenStreamAsync(endpoint, _session, new RemoteStreamRequest(HttpMethod.Get, "/x.ts", null), CancellationToken.None)).Failure,
            (await playback.StopEncodingAsync(endpoint, _session, "ps", CancellationToken.None)).Failure
        };

        Assert.All(results, failure => Assert.Equal(RemoteFailure.RedirectRefused, failure));
        Assert.Empty(other.Requests);
    }

    [Fact]
    public async Task A_redirect_on_the_same_host_is_followed_and_keeps_the_token_there()
    {
        RawHttpServer? self = null;
        using var server = new RawHttpServer((head, stream, token) => RawHttpServer.WriteAsync(
            stream,
            head.StartsWith("GET /UserViews", StringComparison.Ordinal)
                ? "HTTP/1.1 302 Found\r\nConnection: close\r\nContent-Length: 0\r\nLocation: http://127.0.0.1:" + self!.Port + "/moved\r\n\r\n"
                : "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: 12\r\n\r\n{\"Items\":[]}\n",
            token));
        self = server;

        var result = await Catalog().GetLibrariesAsync(EndpointOf(server), _session, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, server.Requests.Count);
        Assert.Contains(TestValues.Token, server.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chunked_json_answer_that_never_ends_is_cut_at_the_limit()
    {
        RawHttpServer? self = null;
        using var server = new RawHttpServer(async (_, stream, token) =>
        {
            await RawHttpServer.WriteAsync(stream, Chunked, token);
            var chunk = new byte[1024 * 1024];
            while (!token.IsCancellationRequested)
            {
                await self!.WriteChunkAsync(stream, chunk, token);
            }
        });
        self = server;

        var result = await WithinAsync(Catalog().GetChangesAsync(EndpointOf(server), _session, null, CancellationToken.None));

        Assert.Equal(RemoteFailure.ResponseTooLarge, result.Failure);
        Assert.InRange(server.BytesWritten, JellyfinRemoteClient.MaxResponseBytes, JellyfinRemoteClient.MaxResponseBytes + (32L * 1024 * 1024));
    }

    [Fact]
    public async Task An_image_has_a_lower_limit_than_json()
    {
        RawHttpServer? self = null;
        using var server = new RawHttpServer(async (_, stream, token) =>
        {
            await RawHttpServer.WriteAsync(stream, ChunkedImage, token);
            var chunk = new byte[1024 * 1024];
            for (var sent = 0; sent <= JellyfinRemoteClient.MaxImageBytes / chunk.Length; sent++)
            {
                await self!.WriteChunkAsync(stream, chunk, token);
            }

            await RawHttpServer.WriteAsync(stream, "0\r\n\r\n", token);
        });
        self = server;

        var result = await WithinAsync(Catalog().GetImageAsync(EndpointOf(server), _session, "item", "Primary", 0, CancellationToken.None));

        Assert.Equal(RemoteFailure.ResponseTooLarge, result.Failure);
    }

    [Fact]
    public async Task A_declared_length_over_the_limit_is_refused_before_any_body_is_read()
    {
        var head = "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: " + (JellyfinRemoteClient.MaxResponseBytes + 1L) + "\r\n\r\n";
        using var server = new RawHttpServer(async (_, stream, token) =>
        {
            await RawHttpServer.WriteAsync(stream, head, token);
            await Task.Delay(Timeout.Infinite, token);
        });

        var result = await WithinAsync(Catalog().GetChangesAsync(EndpointOf(server), _session, null, CancellationToken.None));

        Assert.Equal(RemoteFailure.ResponseTooLarge, result.Failure);
    }

    [Fact]
    public async Task A_json_answer_that_stops_arriving_is_a_timeout()
    {
        using var server = new RawHttpServer(async (_, stream, token) =>
        {
            await RawHttpServer.WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 100\r\n\r\n{\"It", token);
            await Task.Delay(Timeout.Infinite, token);
        });

        var result = await WithinAsync(Catalog(TimeSpan.FromMilliseconds(300)).GetChangesAsync(EndpointOf(server), _session, null, CancellationToken.None));

        Assert.Equal(RemoteFailure.Timeout, result.Failure);
    }

    [Fact]
    public async Task A_login_that_never_answers_is_a_timeout()
    {
        using var server = new RawHttpServer((_, _, token) => Task.Delay(Timeout.Infinite, token));

        var result = await WithinAsync(Catalog(TimeSpan.FromMilliseconds(300)).AuthenticateAsync(EndpointOf(server), TestValues.Credentials, CancellationToken.None));

        Assert.Equal(RemoteFailure.Timeout, result.Failure);
    }

    [Theory]
    [InlineData(401, RemoteFailure.AuthenticationFailed)]
    [InlineData(403, RemoteFailure.Forbidden)]
    [InlineData(500, RemoteFailure.ServerError)]
    public async Task The_body_of_an_error_is_never_read(int status, RemoteFailure expected)
    {
        var head = "HTTP/1.1 " + status + " Nope\r\nContent-Type: text/plain\r\nContent-Length: 2000000000\r\n\r\nstart";
        using var server = new RawHttpServer(async (_, stream, token) =>
        {
            await RawHttpServer.WriteAsync(stream, head, token);
            await Task.Delay(Timeout.Infinite, token);
        });
        var endpoint = EndpointOf(server);

        var catalog = await WithinAsync(Catalog(TimeSpan.FromSeconds(10)).GetChangesAsync(endpoint, _session, null, CancellationToken.None));
        var playback = await WithinAsync(Playback().ReadPlaylistAsync(endpoint, _session, "/x.m3u8", CancellationToken.None));

        Assert.Equal(expected, catalog.Failure);
        Assert.Equal(expected, playback.Failure);
    }

    [Fact]
    public async Task A_playlist_that_never_ends_is_cut_at_the_limit()
    {
        RawHttpServer? self = null;
        using var server = new RawHttpServer(async (_, stream, token) =>
        {
            await RawHttpServer.WriteAsync(stream, Chunked, token);
            var chunk = new byte[1024 * 1024];
            while (!token.IsCancellationRequested)
            {
                await self!.WriteChunkAsync(stream, chunk, token);
            }
        });
        self = server;

        var result = await WithinAsync(Playback().ReadPlaylistAsync(EndpointOf(server), _session, "/x.m3u8", CancellationToken.None));

        Assert.Equal(RemoteFailure.ResponseTooLarge, result.Failure);
        Assert.InRange(server.BytesWritten, RemotePlaybackClient.MaxDocumentBytes, RemotePlaybackClient.MaxDocumentBytes + (32L * 1024 * 1024));
    }

    [Fact]
    public async Task A_stream_that_stops_sending_after_its_headers_ends_at_the_stall_limit()
    {
        using var server = new RawHttpServer(async (_, stream, token) =>
        {
            await RawHttpServer.WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: video/mp4\r\nContent-Length: 100\r\n\r\nabcd", token);
            await Task.Delay(Timeout.Infinite, token);
        });

        using var opened = await Playback().OpenStreamAsync(EndpointOf(server), _session, new RemoteStreamRequest(HttpMethod.Get, "/x.mp4", null), CancellationToken.None) switch
        {
            { Value: { } content } => content,
            _ => throw new InvalidOperationException("The stream did not open.")
        };
        using var target = new System.IO.MemoryStream();

        await Assert.ThrowsAsync<TimeoutException>(() => opened.CopyToAsync(target, TimeSpan.FromMilliseconds(300), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.Equal(4, target.Length);
    }

    [Fact]
    public async Task A_token_refused_on_a_playback_call_logs_in_again_at_once()
    {
        using var server = new RawHttpServer((head, stream, token) => RawHttpServer.WriteAsync(
            stream,
            head.StartsWith("POST /Users/AuthenticateByName", StringComparison.Ordinal)
                ? "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: " + TestValues.LoginJson.Length + "\r\n\r\n" + TestValues.LoginJson
                : "HTTP/1.1 401 X\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: 0\r\n\r\n",
            token));
        var store = new InMemoryConfigurationStore();
        var vault = new Jellyfin.Plugin.JellyMesh.Security.CredentialVault(new FixedKeySource(TestValues.Key), store);
        using var registry = new RemoteServerRegistry(store, vault, Catalog(), new CapturingLogger<RemoteServerRegistry>());
        var added = await registry.AddAsync("Friend", server.BaseUrl.AbsoluteUri, TestValues.Credentials, CancellationToken.None);
        var playback = Playback();
        var request = new RemotePlaybackRequest { ItemId = "i", MediaSourceId = "m" };

        var result = await WithinAsync(registry.RunWithSessionAsync(
            added.Value!.Id,
            (endpoint, session, token) => playback.GetPlaybackInfoAsync(endpoint, session, request, token),
            CancellationToken.None));

        Assert.Equal(RemoteFailure.AuthenticationFailed, result.Failure);
        Assert.Equal(2, server.Requests.Count(line => line.StartsWith("POST /Users/AuthenticateByName", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_connection_refused_is_only_unreachable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var endpoint = new RemoteEndpoint(new Uri("http://127.0.0.1:" + port + "/"), "device");

        var catalog = await Catalog().GetChangesAsync(endpoint, _session, null, CancellationToken.None);
        var playback = await Playback().OpenStreamAsync(endpoint, _session, new RemoteStreamRequest(HttpMethod.Get, "/x.ts", null), CancellationToken.None);

        Assert.Equal(RemoteFailure.Unreachable, catalog.Failure);
        Assert.Equal(RemoteFailure.Unreachable, playback.Failure);
    }

    [Fact]
    public async Task No_call_reaches_a_host_named_in_a_path()
    {
        using var other = new RawHttpServer((_, stream, token) => RawHttpServer.WriteAsync(stream, Ok, token));
        using var remote = new RawHttpServer((_, stream, token) => RawHttpServer.WriteAsync(stream, Ok, token));
        var endpoint = EndpointOf(remote);
        var playback = Playback();
        string[] paths =
        [
            "http://127.0.0.1:" + other.Port + "/x.m3u8",
            "//127.0.0.1:" + other.Port + "/x.m3u8",
            "/\\127.0.0.1:" + other.Port + "/x.m3u8",
            "\\\\127.0.0.1:" + other.Port + "\\x.m3u8",
            "/videos/../../x.m3u8",
            "/videos/%2e%2e/x.m3u8",
            new string('x', 70_000)
        ];

        foreach (var path in paths)
        {
            var playlist = await playback.ReadPlaylistAsync(endpoint, _session, path, CancellationToken.None);
            var stream = await playback.OpenStreamAsync(endpoint, _session, new RemoteStreamRequest(HttpMethod.Get, path, null), CancellationToken.None);

            Assert.Equal(RemoteFailure.InvalidResponse, playlist.Failure);
            Assert.Equal(RemoteFailure.InvalidResponse, stream.Failure);
        }

        Assert.Empty(other.Requests);
        Assert.Empty(remote.Requests);
    }
}
