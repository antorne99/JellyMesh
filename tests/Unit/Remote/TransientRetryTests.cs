using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Remote;

/// <summary>
/// Retries of the sync reads on transient errors (decision 47).
/// </summary>
public class TransientRetryTests
{
    private const string PageJson = "{\"Items\":[],\"TotalRecordCount\":0}";

    private static readonly RemoteEndpoint _endpoint = new(new Uri(TestValues.Url + "/"), "jellymesh-device");
    private static readonly RemoteSession _session = new(TestValues.Token, "abc");
    private static readonly RemoteItemQuery _query = new("lib", RemoteItemType.Movie, 0, 1);

    private readonly List<TimeSpan> _waits = [];

    private JellyfinRemoteClient ClientFor(HttpMessageHandler handler) => new(new StubHttpClientFactory(handler))
    {
        Delay = (wait, _) =>
        {
            _waits.Add(wait);
            return Task.CompletedTask;
        }
    };

    private static HttpResponseMessage Status(int status) => new((HttpStatusCode)status);

    // Answers each request with the next response, repeating the last one.
    private static StubHandler Answers(params Func<HttpResponseMessage>[] responses)
    {
        var count = 0;
        return new StubHandler(_ => responses[Math.Min(count++, responses.Length - 1)]());
    }

    [Theory]
    [InlineData(429)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task A_transient_status_on_a_catalog_page_is_tried_again_after_one_second(int status)
    {
        var handler = Answers(() => Status(status), () => StubHandler.Json(PageJson));

        var result = await ClientFor(handler).GetItemsAsync(_endpoint, _session, _query, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1)], _waits);
    }

    [Fact]
    public async Task A_read_that_keeps_failing_is_tried_twice_more_after_one_and_three_seconds()
    {
        var handler = Answers(() => Status(503));

        var result = await ClientFor(handler).GetItemsAsync(_endpoint, _session, _query, CancellationToken.None);

        Assert.Equal(RemoteFailure.ServerError, result.Failure);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)], _waits);
    }

    [Theory]
    [InlineData(500, RemoteFailure.ServerError)]
    [InlineData(501, RemoteFailure.ServerError)]
    [InlineData(400, RemoteFailure.ServerError)]
    [InlineData(401, RemoteFailure.AuthenticationFailed)]
    [InlineData(403, RemoteFailure.Forbidden)]
    [InlineData(404, RemoteFailure.ServerError)]
    public async Task Other_errors_are_not_tried_again(int status, RemoteFailure expected)
    {
        var handler = Answers(() => Status(status));

        var result = await ClientFor(handler).GetItemsAsync(_endpoint, _session, _query, CancellationToken.None);

        Assert.Equal(expected, result.Failure);
        Assert.Single(handler.Requests);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task A_refused_connection_is_not_tried_again()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("refused"));

        var result = await ClientFor(handler).GetItemsAsync(_endpoint, _session, _query, CancellationToken.None);

        Assert.Equal(RemoteFailure.Unreachable, result.Failure);
        Assert.Single(handler.Requests);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task A_read_that_times_out_is_tried_again()
    {
        var handler = new SlowFirstHandler(StubHandler.Json(PageJson));
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler))
        {
            RequestLimit = TimeSpan.FromMilliseconds(100),
            Delay = (wait, _) =>
            {
                _waits.Add(wait);
                return Task.CompletedTask;
            }
        };

        var result = await client.GetItemsAsync(_endpoint, _session, _query, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Count);
        Assert.Equal([TimeSpan.FromSeconds(1)], _waits);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    [InlineData(60, 10)]
    public async Task Retry_after_lengthens_the_wait_up_to_ten_seconds(int retryAfter, int expected)
    {
        var handler = Answers(
            () =>
            {
                var response = Status(503);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfter));
                return response;
            },
            () => StubHandler.Json(PageJson));

        await ClientFor(handler).GetItemsAsync(_endpoint, _session, _query, CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(expected)], _waits);
    }

    [Fact]
    public async Task Retry_after_as_a_date_counts_from_the_date_of_the_answer()
    {
        var now = new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);
        var handler = Answers(
            () =>
            {
                var response = Status(429);
                response.Headers.Date = now;
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddSeconds(7));
                return response;
            },
            () => StubHandler.Json(PageJson));

        await ClientFor(handler).GetItemsAsync(_endpoint, _session, _query, CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(7)], _waits);
    }

    [Fact]
    public async Task Retry_after_does_not_shorten_the_second_wait()
    {
        var handler = Answers(() =>
        {
            var response = Status(503);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return response;
        });

        await ClientFor(handler).GetItemsAsync(_endpoint, _session, _query, CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)], _waits);
    }

    [Fact]
    public async Task Cancelling_stops_the_wait_at_once_and_sends_nothing_more()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = Answers(() => Status(503));
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler))
        {
            Delay = async (_, token) =>
            {
                await cancellation.CancelAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };

        var read = client.GetItemsAsync(_endpoint, _session, _query, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task The_new_try_sends_the_same_request_with_the_token_in_the_header_only()
    {
        var handler = Answers(() => Status(502), () => StubHandler.Json(PageJson));

        await ClientFor(handler).GetItemsAsync(_endpoint, _session, _query, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests[0].Uri, handler.Requests[1].Uri);
        Assert.DoesNotContain(TestValues.Token, handler.Requests[1].Uri.ToString(), StringComparison.Ordinal);
        Assert.Contains(TestValues.Token, handler.Requests[1].Authorization, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("items")]
    [InlineData("provider items")]
    [InlineData("changes")]
    [InlineData("libraries")]
    [InlineData("shared libraries")]
    [InlineData("server time")]
    [InlineData("capabilities")]
    public async Task Every_read_of_the_sync_is_tried_again(string read)
    {
        var handler = Answers(() => Status(503));
        var client = ClientFor(handler);

        var failure = read switch
        {
            "items" => (await client.GetItemsAsync(_endpoint, _session, _query, CancellationToken.None)).Failure,
            "provider items" => (await client.GetItemsAsync(_endpoint, _session, _query with { ViaProvider = true }, CancellationToken.None)).Failure,
            "changes" => (await client.GetChangesAsync(_endpoint, _session, "c", CancellationToken.None)).Failure,
            "libraries" => (await client.GetLibrariesAsync(_endpoint, _session, CancellationToken.None)).Failure,
            "shared libraries" => (await client.GetSharedLibrariesAsync(_endpoint, _session, CancellationToken.None)).Failure,
            "server time" => (await client.GetServerTimeAsync(_endpoint, _session, CancellationToken.None)).Failure,
            _ => (await client.DetectCapabilitiesAsync(_endpoint, _session, CancellationToken.None)).Failure
        };

        Assert.Equal(RemoteFailure.ServerError, failure);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task A_login_is_never_tried_again()
    {
        var handler = Answers(() => Status(503));

        var result = await ClientFor(handler).AuthenticateAsync(_endpoint, TestValues.Credentials, CancellationToken.None);

        Assert.Equal(RemoteFailure.ServerError, result.Failure);
        Assert.Single(handler.Requests);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task An_image_is_not_tried_again()
    {
        var handler = Answers(() => Status(503));

        var result = await ClientFor(handler).GetImageAsync(_endpoint, _session, "item", "Primary", 0, CancellationToken.None);

        Assert.Equal(RemoteFailure.ServerError, result.Failure);
        Assert.Single(handler.Requests);
        Assert.Empty(_waits);
    }

    // Holds the first request until it is cancelled, then answers the next ones.
    private sealed class SlowFirstHandler(HttpResponseMessage answer) : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++Count == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return answer;
        }
    }
}
