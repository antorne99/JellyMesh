using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Remote;

public class RemoteLyricsClientTests
{
    private static readonly RemoteEndpoint _endpoint = new(new Uri(TestValues.Url + "/"), "device");
    private static readonly RemoteSession _session = new(TestValues.Token, "remote-user");

    private static (RemotePlaybackClient Client, StubHandler Handler) Build(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new RemotePlaybackClient(new StubHttpClientFactory(handler)), handler);
    }

    [Fact]
    public async Task Reads_the_lyrics_of_a_remote_track_with_the_token_in_a_header()
    {
        const string Body = "{\"Metadata\":{\"Artist\":\"Artist\",\"Offset\":5,\"IsSynced\":true},\"Lyrics\":["
            + "{\"Text\":\"One\",\"Start\":10,\"Cues\":[{\"Position\":0,\"EndPosition\":3,\"Start\":10,\"End\":20}]},{\"Text\":\"Two\"}]}";
        var (client, handler) = Build(_ => StubHandler.Json(Body));

        var result = await client.GetLyricsAsync(_endpoint, _session, "item/1", CancellationToken.None);

        var lyrics = result.Value!.Lyrics!;
        Assert.Equal(("Artist", 5L, true), (lyrics.Metadata.Artist, lyrics.Metadata.Offset, lyrics.Metadata.IsSynced));
        Assert.Equal(["One", "Two"], lyrics.Lyrics.Select(line => line.Text));
        Assert.Equal(10, lyrics.Lyrics[0].Start);
        var cue = Assert.Single(lyrics.Lyrics[0].Cues!);
        Assert.Equal((0, 3, 10L, (long?)20), (cue.Position, cue.EndPosition, cue.Start, cue.End));
        Assert.Null(lyrics.Lyrics[1].Start);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/Audio/item%2F1/Lyrics", request.Uri.AbsolutePath);
        Assert.Contains(TestValues.Token, request.Authorization, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Token, request.Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_track_without_lyrics_is_an_answer_with_none()
    {
        var (client, _) = Build(_ => StubHandler.Status(HttpStatusCode.NotFound));

        var result = await client.GetLyricsAsync(_endpoint, _session, "item", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Lyrics);
    }

    [Fact]
    public async Task Only_a_bounded_number_of_lines_is_kept()
    {
        var lines = string.Join(',', Enumerable.Range(0, RemotePlaybackClient.MaxLyricLines + 10).Select(index => "{\"Text\":\"" + index + "\"}"));
        var (client, _) = Build(_ => StubHandler.Json("{\"Lyrics\":[" + lines + "]}"));

        var result = await client.GetLyricsAsync(_endpoint, _session, "item", CancellationToken.None);

        Assert.Equal(RemotePlaybackClient.MaxLyricLines, result.Value!.Lyrics!.Lyrics.Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"Lyrics\":[{\"Text\":5}]}")]
    public async Task A_malformed_answer_is_a_failure(string body)
    {
        var (client, _) = Build(_ => StubHandler.Json(body));

        var result = await client.GetLyricsAsync(_endpoint, _session, "item", CancellationToken.None);

        Assert.Equal(RemoteFailure.InvalidResponse, result.Failure);
    }

    [Fact]
    public async Task A_line_without_text_is_left_out()
    {
        var (client, _) = Build(_ => StubHandler.Json("{\"Lyrics\":[{\"Start\":1},null,{\"Text\":\"Two\"}]}"));

        var result = await client.GetLyricsAsync(_endpoint, _session, "item", CancellationToken.None);

        Assert.Equal(["Two"], result.Value!.Lyrics!.Lyrics.Select(line => line.Text));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, RemoteFailure.ServerError)]
    [InlineData(HttpStatusCode.Forbidden, RemoteFailure.Forbidden)]
    public async Task Another_error_is_a_failure(HttpStatusCode status, RemoteFailure failure)
    {
        var (client, _) = Build(_ => StubHandler.Status(status));

        var result = await client.GetLyricsAsync(_endpoint, _session, "item", CancellationToken.None);

        Assert.Equal(failure, result.Failure);
    }
}
