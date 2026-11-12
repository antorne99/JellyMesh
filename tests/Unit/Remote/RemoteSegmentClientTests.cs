using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Remote;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Remote;

public class RemoteSegmentClientTests
{
    private static readonly RemoteEndpoint _endpoint = new(new Uri(TestValues.Url + "/"), "device");
    private static readonly RemoteSession _session = new(TestValues.Token, "remote-user");

    private static (RemotePlaybackClient Client, StubHandler Handler) Build(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new RemotePlaybackClient(new StubHttpClientFactory(handler)), handler);
    }

    private static string Segment(string type, long start, long end) =>
        "{\"Id\":\"" + Guid.NewGuid().ToString("N") + "\",\"ItemId\":\"remote\",\"Type\":\"" + type + "\",\"StartTicks\":" + start + ",\"EndTicks\":" + end + "}";

    private static string Page(params string[] segments) => "{\"Items\":[" + string.Join(',', segments) + "],\"TotalRecordCount\":" + segments.Length + "}";

    [Fact]
    public async Task Reads_the_segments_of_a_remote_item_with_the_token_in_a_header()
    {
        var (client, handler) = Build(_ => StubHandler.Json(Page(Segment("Intro", 0, 900), Segment("Outro", 5000, 6000), Segment("Recap", 1, 2), Segment("Preview", 3, 4), Segment("Commercial", 7, 8))));

        var result = await client.GetSegmentsAsync(_endpoint, _session, "item/1", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [new RemoteSegment(MediaSegmentType.Intro, 0, 900), new RemoteSegment(MediaSegmentType.Outro, 5000, 6000), new RemoteSegment(MediaSegmentType.Recap, 1, 2), new RemoteSegment(MediaSegmentType.Preview, 3, 4), new RemoteSegment(MediaSegmentType.Commercial, 7, 8)],
            result.Value!);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/MediaSegments/item%2F1", request.Uri.AbsolutePath);
        Assert.Contains(TestValues.Token, request.Authorization, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Token, request.Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_segments_is_an_empty_answer()
    {
        var (client, _) = Build(_ => StubHandler.Json(Page()));

        var result = await client.GetSegmentsAsync(_endpoint, _session, "item", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    }

    [Theory]
    [InlineData("Unknown", 0, 10)]
    [InlineData("Credits", 0, 10)]
    [InlineData("5", 0, 10)]
    [InlineData("Intro", -1, 10)]
    [InlineData("Intro", 10, 10)]
    [InlineData("Intro", 10, 5)]
    public async Task A_segment_of_an_unknown_type_or_with_bad_times_is_left_out(string type, long start, long end)
    {
        var (client, _) = Build(_ => StubHandler.Json(Page(Segment(type, start, end), Segment("Intro", 0, 900))));

        var result = await client.GetSegmentsAsync(_endpoint, _session, "item", CancellationToken.None);

        Assert.Equal([new RemoteSegment(MediaSegmentType.Intro, 0, 900)], result.Value!);
    }

    [Fact]
    public async Task Only_a_bounded_number_of_segments_is_kept()
    {
        var many = Enumerable.Range(0, RemotePlaybackClient.MaxSegments + 10).Select(index => Segment("Commercial", index * 10L, (index * 10L) + 5)).ToArray();
        var (client, _) = Build(_ => StubHandler.Json(Page(many)));

        var result = await client.GetSegmentsAsync(_endpoint, _session, "item", CancellationToken.None);

        Assert.Equal(RemotePlaybackClient.MaxSegments, result.Value!.Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Items\":[{\"Type\":3,\"StartTicks\":\"x\"}]}")]
    [InlineData("[]")]
    public async Task A_malformed_answer_is_a_failure(string body)
    {
        var (client, _) = Build(_ => StubHandler.Json(body));

        var result = await client.GetSegmentsAsync(_endpoint, _session, "item", CancellationToken.None);

        Assert.Equal(RemoteFailure.InvalidResponse, result.Failure);
    }

    [Fact]
    public async Task A_refusal_is_a_failure()
    {
        var (client, _) = Build(_ => StubHandler.Status(HttpStatusCode.Forbidden));

        var result = await client.GetSegmentsAsync(_endpoint, _session, "item", CancellationToken.None);

        Assert.Equal(RemoteFailure.Forbidden, result.Failure);
    }
}
