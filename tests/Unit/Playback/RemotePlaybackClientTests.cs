using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

public class RemotePlaybackClientTests
{
    private const string PlaybackJson =
        "{\"MediaSources\":[{\"Id\":\"ms\",\"Protocol\":\"File\",\"SupportsDirectPlay\":false,\"TranscodingUrl\":\"/videos/x/master.m3u8\","
        + "\"TranscodingSubProtocol\":\"hls\",\"MediaStreams\":[{\"Index\":2,\"Type\":\"Video\",\"Codec\":\"h264\"}]}],\"PlaySessionId\":\"ps\"}";

    private static readonly RemoteEndpoint _endpoint = new(new Uri(TestValues.Url + "/"), "device");
    private static readonly RemoteSession _session = new(TestValues.Token, "remote-user");

    private static RemotePlaybackRequest Request() => new() { ItemId = "item", MediaSourceId = "ms", AudioStreamIndex = 3, DeviceProfile = new DeviceProfile { Name = "web" } };

    private static (RemotePlaybackClient Client, StubHandler Handler) Build(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new RemotePlaybackClient(new StubHttpClientFactory(handler)), handler);
    }

    [Fact]
    public async Task PlaybackInfo_forwards_the_profile_and_reads_the_answer()
    {
        var (client, handler) = Build(_ => StubHandler.Json(PlaybackJson));

        var result = await client.GetPlaybackInfoAsync(_endpoint, _session, Request(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("ps", result.Value!.PlaySessionId);
        Assert.Equal("ms", result.Value.MediaSources[0].Id);
        Assert.Equal(2, result.Value.MediaSources[0].MediaStreams[0].Index);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/Items/item/PlaybackInfo", request.Uri.AbsolutePath);
        Assert.Contains("\"DeviceProfile\":{", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"Name\":\"web\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"MediaSourceId\":\"ms\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"AudioStreamIndex\":3", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"UserId\":\"remote-user\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_goes_in_a_header_and_never_in_a_url()
    {
        var (client, handler) = Build(_ => StubHandler.Json("{}"));

        await client.ReadPlaylistAsync(_endpoint, _session, "/videos/x/master.m3u8?a=1", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Contains(TestValues.Token, request.Authorization, StringComparison.Ordinal);
        Assert.Contains("DeviceId=\"device\"", request.Authorization, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Token, request.Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_request_carries_the_same_user_agent()
    {
        string? seen = null;
        var (client, _) = Build(request =>
        {
            seen = request.Headers.UserAgent.ToString();
            return StubHandler.Json("{}");
        });

        await client.ReadPlaylistAsync(_endpoint, _session, "/x.m3u8", CancellationToken.None);

        Assert.Equal(RemotePlaybackClient.UserAgent, seen);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found, RemoteFailure.RedirectRefused)]
    [InlineData(HttpStatusCode.TemporaryRedirect, RemoteFailure.RedirectRefused)]
    [InlineData(HttpStatusCode.Unauthorized, RemoteFailure.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, RemoteFailure.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError, RemoteFailure.ServerError)]
    [InlineData(HttpStatusCode.NotFound, RemoteFailure.ServerError)]
    public async Task A_stream_failure_is_only_a_kind_of_failure(HttpStatusCode status, RemoteFailure expected)
    {
        var (client, handler) = Build(_ =>
        {
            var response = StubHandler.Status(status);
            response.Headers.TryAddWithoutValidation("Location", "http://other.example/x");
            response.Content = new StringContent("secret error body");
            return response;
        });

        var result = await client.OpenStreamAsync(_endpoint, _session, new RemoteStreamRequest(HttpMethod.Get, "/x.ts", null), CancellationToken.None);

        Assert.Equal(expected, result.Failure);
        Assert.Null(result.Value);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_document_refused_with_a_valid_token_is_not_a_problem_of_the_login()
    {
        var (client, _) = Build(_ => StubHandler.Status(HttpStatusCode.Forbidden));

        Assert.Equal(RemoteFailure.Forbidden, (await client.ReadPlaylistAsync(_endpoint, _session, "/x.m3u8", CancellationToken.None)).Failure);
        Assert.Equal(RemoteFailure.Forbidden, (await client.StopEncodingAsync(_endpoint, _session, "ps", CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task A_stream_passes_range_and_keeps_only_four_headers()
    {
        string? range = null;
        var (client, _) = Build(request =>
        {
            range = request.Headers.Range?.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[10]) };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "video/webm");
            response.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 0-9/100");
            response.Headers.TryAddWithoutValidation("Accept-Ranges", "bytes");
            response.Headers.TryAddWithoutValidation("Server", "remote-name");
            return response;
        });

        var result = await client.OpenStreamAsync(_endpoint, _session, new RemoteStreamRequest(HttpMethod.Get, "/s", "bytes=0-9"), CancellationToken.None);

        using var content = result.Value!;
        Assert.Equal("bytes=0-9", range);
        Assert.Equal(HttpStatusCode.PartialContent, content.Status);
        Assert.Equal(new RemoteContentHeaders("video/webm", 10, "bytes 0-9/100", "bytes"), content.Headers);
    }

    [Fact]
    public async Task A_range_past_the_end_reaches_the_client()
    {
        var (client, _) = Build(_ => StubHandler.Status(HttpStatusCode.RequestedRangeNotSatisfiable));

        var result = await client.OpenStreamAsync(_endpoint, _session, new RemoteStreamRequest(HttpMethod.Get, "/s", "bytes=99-"), CancellationToken.None);

        using var content = result.Value!;
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, content.Status);
    }

    [Fact]
    public async Task Stop_names_the_device_and_the_play_session_and_no_item()
    {
        var (client, handler) = Build(_ => StubHandler.Status(HttpStatusCode.NoContent));

        var result = await client.StopEncodingAsync(_endpoint, _session, "ps", CancellationToken.None);

        Assert.True(result.IsSuccess);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/Videos/ActiveEncodings?DeviceId=device&PlaySessionId=ps", request.Uri.PathAndQuery);
    }

    [Fact]
    public async Task The_policy_comes_from_the_user_and_the_encoding_configuration()
    {
        var (client, _) = Build(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? StubHandler.Json("{\"Policy\":{\"EnableVideoPlaybackTranscoding\":false,\"EnableAudioPlaybackTranscoding\":true,\"EnablePlaybackRemuxing\":true}}")
            : StubHandler.Json("{\"HardwareAccelerationType\":\"qsv\"}"));

        var result = await client.GetTranscodePolicyAsync(_endpoint, _session, true, CancellationToken.None);

        Assert.Equal(new RemoteTranscodePolicy("qsv", false, true, true), result.Value);
    }

    [Fact]
    public async Task A_policy_without_the_encoding_configuration_still_counts()
    {
        var (client, _) = Build(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? StubHandler.Json("{\"Policy\":{\"EnableVideoPlaybackTranscoding\":true,\"EnableAudioPlaybackTranscoding\":true,\"EnablePlaybackRemuxing\":true}}")
            : StubHandler.Status(HttpStatusCode.Forbidden));

        var result = await client.GetTranscodePolicyAsync(_endpoint, _session, true, CancellationToken.None);

        Assert.Null(result.Value!.HardwareAccelerationType);
    }

    [Fact]
    public async Task A_policy_without_the_hardware_never_touches_the_configuration()
    {
        var paths = new List<string>();
        var (client, _) = Build(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return StubHandler.Json("{\"Policy\":{\"EnableVideoPlaybackTranscoding\":true,\"EnableAudioPlaybackTranscoding\":true,\"EnablePlaybackRemuxing\":true}}");
        });

        var result = await client.GetTranscodePolicyAsync(_endpoint, _session, false, CancellationToken.None);

        Assert.Equal(["/Users/Me"], paths);
        Assert.Equal(new RemoteTranscodePolicy(null, true, true, true), result.Value);
    }

    [Theory]
    [InlineData("{\"MediaSources\":null}")]
    [InlineData("{\"MediaSources\":[null]}")]
    [InlineData("{\"MediaSources\":[{\"Id\":\"ms\",\"MediaStreams\":[null]}]}")]
    [InlineData("{\"MediaSources\":[{\"Id\":\"ms\",\"Protocol\":\"NotAProtocol\"}]}")]
    [InlineData("{\"MediaSources\":[{\"Id\":\"ms\",\"Size\":\"huge\"}]}")]
    [InlineData("{\"MediaSources\":{}}")]
    [InlineData("{\"MediaSources\":[],\"PlaySessionId\":\"a\\r\\nX-Injected: 1\"}")]
    [InlineData("not json")]
    public async Task A_playback_answer_of_the_wrong_shape_is_refused_and_is_no_crash(string json)
    {
        var (client, _) = Build(_ => StubHandler.Json(json));

        var result = await client.GetPlaybackInfoAsync(_endpoint, _session, Request(), CancellationToken.None);

        Assert.Equal(RemoteFailure.InvalidResponse, result.Failure);
    }

    [Fact]
    public async Task An_oversized_document_is_refused()
    {
        var (client, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[RemotePlaybackClient.MaxDocumentBytes + 1]) });

        var result = await client.ReadPlaylistAsync(_endpoint, _session, "/x.m3u8", CancellationToken.None);

        Assert.Equal(RemoteFailure.ResponseTooLarge, result.Failure);
    }

    [Fact]
    public async Task A_broken_connection_says_nothing_of_the_address()
    {
        var factory = new StubHttpClientFactory(new ThrowingHandler());
        var client = new RemotePlaybackClient(factory);

        var result = await client.ReadPlaylistAsync(_endpoint, _session, "/x.m3u8", CancellationToken.None);

        Assert.Equal(RemoteFailure.Unreachable, result.Failure);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("connection to " + TestValues.Host + " failed");
        }
    }
}
