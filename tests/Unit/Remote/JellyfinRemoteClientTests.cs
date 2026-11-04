using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Remote;

public class JellyfinRemoteClientTests
{
    private static readonly RemoteEndpoint _endpoint = new(new Uri(TestValues.Url + "/"), "jellymesh-device");
    private static readonly RemoteSession _session = new(TestValues.Token, "abc");

    private static JellyfinRemoteClient ClientFor(StubHandler handler) => new(new StubHttpClientFactory(handler));

    private static Task<RemoteResult<RemoteSession>> LoginAsync(StubHandler handler)
        => ClientFor(handler).AuthenticateAsync(_endpoint, TestValues.Credentials, CancellationToken.None);

    [Fact]
    public async Task LoginPostsAuthenticateByNameWithPascalCaseAcceptAndNoToken()
    {
        var handler = StubHandler.StockServer();

        var result = await LoginAsync(handler);

        Assert.Equal(TestValues.Token, result.Value!.AccessToken);
        Assert.Equal("abc", result.Value.UserId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(TestValues.Url + "/Users/AuthenticateByName", request.Uri.ToString());
        Assert.Contains("profile=\"PascalCase\"", request.Accept, StringComparison.Ordinal);
        Assert.StartsWith("MediaBrowser Client=\"JellyMesh\"", request.Authorization, StringComparison.Ordinal);
        Assert.Contains("DeviceId=\"jellymesh-device\"", request.Authorization, StringComparison.Ordinal);
        Assert.DoesNotContain("Token=", request.Authorization, StringComparison.Ordinal);
        Assert.Contains("\"Username\":\"" + TestValues.Username + "\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"Pw\":\"" + TestValues.Password + "\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnImageIsReadWithTheSessionInTheHeaderOnly()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return response;
        });

        var result = await ClientFor(handler).GetImageAsync(_endpoint, _session, "item", "Primary", 0, CancellationToken.None);

        Assert.Equal(bytes, result.Value!.Content.ToArray());
        Assert.Equal("image/png", result.Value.ContentType);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/Items/item/Images/Primary/0", request.Uri.AbsolutePath);
        Assert.DoesNotContain(TestValues.Token, request.Uri.ToString(), StringComparison.Ordinal);
        Assert.Contains(TestValues.Token, request.Authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerThatIsNotAnImageIsRefused()
    {
        var handler = new StubHandler(_ => StubHandler.Json("{}"));

        var result = await ClientFor(handler).GetImageAsync(_endpoint, _session, "item", "Primary", 0, CancellationToken.None);

        Assert.Equal(RemoteFailure.InvalidResponse, result.Failure);
    }

    [Fact]
    public async Task LoginAcceptsCamelCaseAnswers()
    {
        var handler = new StubHandler(_ => StubHandler.Json(
            "{\"accessToken\":\"t\",\"user\":{\"id\":\"u\",\"policy\":{\"isAdministrator\":false}}}"));

        var result = await LoginAsync(handler);

        Assert.Equal("t", result.Value!.AccessToken);
    }

    [Fact]
    public async Task LoginKeepsABasePathOfTheUrl()
    {
        var handler = StubHandler.StockServer();
        var endpoint = new RemoteEndpoint(new Uri("http://server/jellyfin/"), "d");

        await ClientFor(handler).AuthenticateAsync(endpoint, TestValues.Credentials, CancellationToken.None);

        Assert.Equal("http://server/jellyfin/Users/AuthenticateByName", handler.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task AnAdministratorAccountIsRefused()
    {
        var handler = new StubHandler(_ => StubHandler.Json(
            "{\"AccessToken\":\"t\",\"User\":{\"Id\":\"u\",\"Policy\":{\"IsAdministrator\":true}}}"));

        var result = await LoginAsync(handler);

        Assert.Equal(RemoteFailure.AdministratorAccount, result.Failure);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("{\"AccessToken\":\"t\",\"User\":{\"Id\":\"u\"}}")] // no policy: cannot tell it is not an admin
    [InlineData("{\"User\":{\"Id\":\"u\",\"Policy\":{\"IsAdministrator\":false}}}")]
    [InlineData("{}")]
    [InlineData("<html>not jellyfin</html>")]
    public async Task AnUnexpectedLoginAnswerIsInvalid(string body)
    {
        var result = await LoginAsync(new StubHandler(_ => StubHandler.Json(body)));

        Assert.Equal(RemoteFailure.InvalidResponse, result.Failure);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, RemoteFailure.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, RemoteFailure.AuthenticationFailed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, RemoteFailure.ServerError)]
    [InlineData(HttpStatusCode.InternalServerError, RemoteFailure.ServerError)]
    public async Task HttpErrorsMapToFailures(HttpStatusCode status, RemoteFailure expected)
    {
        var result = await LoginAsync(new StubHandler(_ => StubHandler.Status(status)));

        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public async Task ConnectionErrorsBecomeUnreachableWithoutTheirMessage()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused (" + TestValues.Host + ":8096)"));

        var result = await LoginAsync(handler);

        Assert.Equal(RemoteFailure.Unreachable, result.Failure);
    }

    [Fact]
    public async Task HttpClientTimeoutsBecomeTimeout()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timed out"));

        var result = await LoginAsync(handler);

        Assert.Equal(RemoteFailure.Timeout, result.Failure);
    }

    [Fact]
    public async Task CallerCancellationIsNotATimeout()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new StubHandler(_ => throw new TaskCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ClientFor(handler).AuthenticateAsync(_endpoint, TestValues.Credentials, cts.Token));
    }

    [Fact]
    public async Task ARedirectToAnotherHostIsNotFollowedAndTheTokenStaysHome()
    {
        var handler = new StubHandler(_ => StubHandler.Redirect(HttpStatusCode.Found, "http://evil.example/UserViews"));

        var result = await ClientFor(handler).GetLibrariesAsync(_endpoint, _session, CancellationToken.None);

        Assert.Equal(RemoteFailure.RedirectRefused, result.Failure);
        Assert.Equal(TestValues.Host, Assert.Single(handler.Requests).Uri.Host);
    }

    [Theory]
    [InlineData("http://friend.tailnet.ts.net:9999/x")]  // other port
    [InlineData("https://friend.tailnet.ts.net:8096/x")] // other scheme
    public async Task ARedirectToAnotherOriginOnTheSameHostIsNotFollowed(string location)
    {
        var handler = new StubHandler(_ => StubHandler.Redirect(HttpStatusCode.TemporaryRedirect, location));

        var result = await LoginAsync(handler);

        Assert.Equal(RemoteFailure.RedirectRefused, result.Failure);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ASameOriginRedirectIsFollowed()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath == "/UserViews"
            ? StubHandler.Redirect(HttpStatusCode.Found, "/jellyfin/UserViews")
            : StubHandler.Json(TestValues.ViewsJson));

        var result = await ClientFor(handler).GetLibrariesAsync(_endpoint, _session, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Contains(TestValues.Token, request.Authorization, StringComparison.Ordinal));
    }

    [Fact]
    public async Task APostIsOnlyRedirectedWhenTheMethodIsKept()
    {
        var moved = new StubHandler(_ => StubHandler.Redirect(HttpStatusCode.Found, "/other"));
        Assert.Equal(RemoteFailure.RedirectRefused, (await LoginAsync(moved)).Failure);
        Assert.Single(moved.Requests);

        var kept = new StubHandler(request => request.RequestUri!.AbsolutePath == "/other"
            ? StubHandler.Json(TestValues.LoginJson)
            : StubHandler.Redirect(HttpStatusCode.TemporaryRedirect, "/other"));
        Assert.True((await LoginAsync(kept)).IsSuccess);
        Assert.All(kept.Requests, request => Assert.Equal(HttpMethod.Post, request.Method));
        Assert.All(kept.Requests, request => Assert.NotNull(request.Body));
    }

    [Fact]
    public async Task ARedirectLoopEnds()
    {
        var handler = new StubHandler(_ => StubHandler.Redirect(HttpStatusCode.Found, "/again"));

        var result = await ClientFor(handler).GetLibrariesAsync(_endpoint, _session, CancellationToken.None);

        Assert.Equal(RemoteFailure.RedirectRefused, result.Failure);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task A404OnTheCapabilityEndpointMeansStandard()
    {
        var handler = StubHandler.StockServer();

        var result = await ClientFor(handler).DetectCapabilitiesAsync(_endpoint, _session, CancellationToken.None);

        Assert.Equal(RemoteMode.Standard, result.Value!.Mode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(TestValues.Url + JellyfinRemoteClient.CapabilitiesPath, request.Uri.ToString());
        Assert.Contains("Token=\"" + TestValues.Token + "\"", request.Authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACapabilityAnswerMeansJellyMesh()
    {
        var handler = new StubHandler(_ => StubHandler.Json("{\"ProtocolVersion\":1}"));

        var result = await ClientFor(handler).DetectCapabilitiesAsync(_endpoint, _session, CancellationToken.None);

        Assert.Equal(new RemoteCapabilities(RemoteMode.JellyMesh, 1), result.Value);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, RemoteFailure.ServerError)]
    [InlineData(HttpStatusCode.Unauthorized, RemoteFailure.AuthenticationFailed)]
    public async Task OnlyA404MeansStandard(HttpStatusCode status, RemoteFailure expected)
    {
        var handler = new StubHandler(_ => StubHandler.Status(status));

        var result = await ClientFor(handler).DetectCapabilitiesAsync(_endpoint, _session, CancellationToken.None);

        Assert.Equal(expected, result.Failure);
    }

    [Theory]
    [InlineData("<html>a web page</html>")]
    [InlineData("{}")]
    public async Task AnUnrelatedSuccessAnswerIsNotJellyMesh(string body)
    {
        var handler = new StubHandler(_ => StubHandler.Json(body));

        var result = await ClientFor(handler).DetectCapabilitiesAsync(_endpoint, _session, CancellationToken.None);

        Assert.Equal(RemoteFailure.InvalidResponse, result.Failure);
    }

    [Fact]
    public async Task LibrariesComeFromUserViewsAndKeepOnlyFilmsAndSeries()
    {
        var handler = StubHandler.StockServer();

        var result = await ClientFor(handler).GetLibrariesAsync(_endpoint, _session, CancellationToken.None);

        Assert.Equal(["Film", "Serie"], result.Value!.Select(library => library.Name));
        Assert.Equal(TestValues.Url + "/UserViews", Assert.Single(handler.Requests).Uri.ToString());
    }

    [Fact]
    public async Task TheTokenIsNeverInAUrl()
    {
        var handler = StubHandler.StockServer();
        var client = ClientFor(handler);

        await client.GetLibrariesAsync(_endpoint, _session, CancellationToken.None);
        await client.DetectCapabilitiesAsync(_endpoint, _session, CancellationToken.None);

        Assert.All(handler.Requests, request => Assert.DoesNotContain(TestValues.Token, request.Uri.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void ToStringOfSecretHoldersPrintsNoSecret()
    {
        Assert.DoesNotContain(TestValues.Token, _session.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Password, TestValues.Credentials.ToString(), StringComparison.Ordinal);
    }

    private static Task<RemoteResult<RemoteItemPage>> ItemsAsync(StubHandler handler, RemoteItemQuery? query = null)
        => ClientFor(handler).GetItemsAsync(_endpoint, _session, query ?? new RemoteItemQuery("lib", RemoteItemType.Episode, 400, 200), CancellationToken.None);

    [Fact]
    public async Task ItemsAsksForOnePageOfTheLibraryWithEverythingTheIndexKeeps()
    {
        var handler = new StubHandler(_ => StubHandler.Json("{\"Items\":[],\"TotalRecordCount\":0}"));

        await ItemsAsync(handler);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/Items", request.Uri.AbsolutePath);
        var query = request.Uri.Query;
        Assert.Contains("userId=abc", query, StringComparison.Ordinal);
        Assert.Contains("parentId=lib", query, StringComparison.Ordinal);
        Assert.Contains("recursive=true", query, StringComparison.Ordinal);
        Assert.Contains("includeItemTypes=Episode", query, StringComparison.Ordinal);
        Assert.Contains("startIndex=400", query, StringComparison.Ordinal);
        Assert.Contains("limit=200", query, StringComparison.Ordinal);
        Assert.Contains("excludeLocationTypes=Virtual", query, StringComparison.Ordinal);
        foreach (var field in new[] { "ProviderIds", "MediaSources", "DateCreated", "Etag", "Overview", "Genres", "People" })
        {
            Assert.Contains(field, query, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(TestValues.Token, request.Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItemsLooksUpIdsWithoutALibrary()
    {
        var handler = new StubHandler(_ => StubHandler.Json("{\"Items\":[],\"TotalRecordCount\":0}"));

        await ItemsAsync(handler, new RemoteItemQuery(null, RemoteItemType.Series, 0, 2, ["a", "b"]));

        var query = handler.Requests[0].Uri.Query;
        Assert.Contains("ids=a,b", query, StringComparison.Ordinal);
        Assert.DoesNotContain("parentId", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItemsParsesARealAnswer()
    {
        const string Json = """
            {"Items":[{"Name":"JM","Id":"69df","Etag":"ab19","DateCreated":"2026-11-04T00:12:04.1706592Z","Overview":"Plot",
              "ProductionYear":2026,"CommunityRating":7.5,"PremiereDate":"2026-01-01T00:00:00.0000000Z","RunTimeTicks":595190000,
              "IndexNumber":2,"ParentIndexNumber":1,"SeriesId":"s","SeriesName":"Show","SeasonId":"ss","SeasonName":"Season 1",
              "ProviderIds":{"Tmdb":"1","Tvdb":null},"Genres":["Drama"],"Studios":[{"Name":"Studio","Id":"x"}],
              "People":[{"Name":"N","Role":"R","Type":"Actor","Id":"p"}],"ImageTags":{"Primary":"t"},"BackdropImageTags":["b"],
              "MediaSources":[{"Protocol":"File","Id":"src","Path":"/media/a.mkv","Container":"mkv","Size":30378097,"Name":"V",
                "IsRemote":false,"ETag":"e","RunTimeTicks":595190000,"Bitrate":4083000,
                "MediaStreams":[{"Codec":"h264","VideoRange":"SDR","VideoRangeType":"SDR","BitRate":3699146,"Height":1080,"Width":1920,
                  "Type":"Video","IsDefault":true}]}],
              "UserData":{"Key":"x"},"LocationType":"FileSystem"}],"TotalRecordCount":11,"StartIndex":0}
            """;
        var handler = new StubHandler(_ => StubHandler.Json(Json));

        var result = await ItemsAsync(handler);

        Assert.Equal(11, result.Value!.TotalRecordCount);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal("69df", item.Id);
        Assert.Equal("Show", item.SeriesName);
        Assert.Equal(7.5f, item.CommunityRating);
        Assert.Equal("Studio", item.Studios![0].Name);
        var stream = item.MediaSources![0].MediaStreams![0];
        Assert.Equal((1080, 3699146, "SDR"), (stream.Height, stream.BitRate, stream.VideoRange));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Items\":[]}")]
    [InlineData("{\"TotalRecordCount\":1}")]
    [InlineData("{\"Items\":[{\"Name\":\"no id\"}],\"TotalRecordCount\":1}")]
    [InlineData("not json")]
    public async Task ItemsRefusesAnAnswerThatIsNotAPage(string json)
    {
        var result = await ItemsAsync(new StubHandler(_ => StubHandler.Json(json)));

        Assert.Equal(RemoteFailure.InvalidResponse, result.Failure);
    }

    [Fact]
    public async Task ItemsReportsATokenRefusedSoTheRegistryCanLogInAgain()
    {
        var result = await ItemsAsync(new StubHandler(_ => StubHandler.Status(HttpStatusCode.Unauthorized)));

        Assert.Equal(RemoteFailure.AuthenticationFailed, result.Failure);
    }

    [Fact]
    public async Task AnAnswerOverTheBufferLimitIsTooLargeAndNotUnreachable()
    {
        var handler = new StubHandler(_ => StubHandler.Json("{\"Items\":[],\"TotalRecordCount\":0,\"Padding\":\"" + new string('x', 4096) + "\"}"));
        var client = new JellyfinRemoteClient(new LimitedClientFactory(handler, 1024));

        var result = await client.GetItemsAsync(_endpoint, _session, new RemoteItemQuery("lib", RemoteItemType.Movie, 0, 1), CancellationToken.None);

        Assert.Equal(RemoteFailure.ResponseTooLarge, result.Failure);
    }

    private sealed class LimitedClientFactory : IHttpClientFactory
    {
        private readonly StubHandler _handler;
        private readonly int _limit;

        public LimitedClientFactory(StubHandler handler, int limit)
        {
            _handler = handler;
            _limit = limit;
        }

        public HttpClient CreateClient(string name)
            => new(_handler, disposeHandler: false) { MaxResponseContentBufferSize = _limit };
    }

    [Fact]
    public async Task AReadOfChangesAsksForThemByLastSavedDateInUtc()
    {
        var handler = new StubHandler(_ => StubHandler.Json("{\"Items\":[],\"TotalRecordCount\":0}"));
        var since = new DateTimeOffset(2026, 11, 4, 4, 9, 14, TimeSpan.FromHours(2));

        await ItemsAsync(handler, new RemoteItemQuery("lib", RemoteItemType.Movie, 0, 10, null, since));

        Assert.Contains("minDateLastSaved=2026-11-04T02:09:14Z", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFullReadDoesNotFilterByDate()
    {
        var handler = new StubHandler(_ => StubHandler.Json("{\"Items\":[],\"TotalRecordCount\":0}"));

        await ItemsAsync(handler);

        Assert.DoesNotContain("minDateLastSaved", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheClockOfTheRemoteComesFromItsDateHeader()
    {
        var date = new DateTimeOffset(2026, 11, 4, 2, 9, 13, TimeSpan.Zero);
        var handler = new StubHandler(_ =>
        {
            var response = StubHandler.Json("{}");
            response.Headers.Date = date;
            return response;
        });

        var result = await ClientFor(handler).GetServerTimeAsync(_endpoint, _session, CancellationToken.None);

        Assert.Equal(date, result.Value);
        Assert.DoesNotContain(_session.AccessToken, handler.Requests[0].Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemoteWithoutADateHeaderGivesNoClock()
    {
        var handler = new StubHandler(_ => StubHandler.Json("{}"));

        var result = await ClientFor(handler).GetServerTimeAsync(_endpoint, _session, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task AnUnreachableRemoteGivesNoClockAndNoAddress()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("No route to " + TestValues.Host));

        var result = await ClientFor(handler).GetServerTimeAsync(_endpoint, _session, CancellationToken.None);

        Assert.Equal(RemoteFailure.Unreachable, result.Failure);
    }
}
