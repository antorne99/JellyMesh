using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Remote;

public class RemoteErrorTests
{
    private const string OtherToken = "another-token-of-someone";

    private static readonly RemoteEndpoint _endpoint = new(new Uri(TestValues.Url + "/"), "device");
    private static readonly RemoteSession _session = new(TestValues.Token, "user");

    [Fact]
    public async Task AnErrorStatusSaysWhatWasAskedAndWhatTheRemoteAnswered()
    {
        var handler = new StubHandler(_ => StubHandler.Json("{\"title\":\"Database is locked\"}", HttpStatusCode.InternalServerError));

        var result = await ClientFor(handler).AuthenticateAsync(_endpoint, TestValues.Credentials, CancellationToken.None);

        Assert.Equal(RemoteFailure.ServerError, result.Failure);
        Assert.Equal(500, result.Status);
        var detail = result.Error!.ToString();
        Assert.Contains("POST " + TestValues.Url + "/Users/AuthenticateByName", detail, StringComparison.Ordinal);
        Assert.Contains("HTTP 500 Internal Server Error", detail, StringComparison.Ordinal);
        Assert.Contains("Database is locked", detail, StringComparison.Ordinal);
        Assert.Contains(" ms", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATransportFailureCarriesTheExceptionChainWithItsStackTrace()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("outer refusal", new InvalidOperationException("inner refusal")));

        var result = await ClientFor(handler).AuthenticateAsync(_endpoint, TestValues.Credentials, CancellationToken.None);

        Assert.Equal(RemoteFailure.Unreachable, result.Failure);
        var detail = result.Error!.ToString();
        Assert.Contains("System.Net.Http.HttpRequestException: outer refusal", detail, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: inner refusal", detail, StringComparison.Ordinal);
        Assert.Contains("   at ", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerThatDoesNotParseCarriesTheParsingErrorAndTheBody()
    {
        var handler = new StubHandler(_ => StubHandler.Json("<html>welcome to the proxy</html>"));

        var result = await ClientFor(handler).GetLibrariesAsync(_endpoint, _session, CancellationToken.None);

        Assert.Equal(RemoteFailure.InvalidResponse, result.Failure);
        var detail = result.Error!.ToString();
        Assert.Contains("JsonException", detail, StringComparison.Ordinal);
        Assert.Contains("welcome to the proxy", detail, StringComparison.Ordinal);
        Assert.Contains("HTTP 200", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TestValues.Token)]
    [InlineData("Authorization: MediaBrowser Token=\"" + OtherToken + "\"")]
    [InlineData("{\"AccessToken\":\"" + OtherToken + "\"}")]
    [InlineData("{\\\"accessToken\\\": \\\"" + OtherToken + "\\\"}")]
    [InlineData("http://x/?api_key=" + OtherToken + "&a=b")]
    [InlineData("http://x/?ApiKey=" + OtherToken)]
    [InlineData("X-Emby-Token: " + OtherToken)]
    [InlineData("X-MediaBrowser-Token=" + OtherToken)]
    [InlineData("Authorization: Bearer " + OtherToken)]
    [InlineData("{\"Username\":\"a\",\"Pw\":\"" + OtherToken + "\"}")]
    [InlineData("\x5cu0022Pw\x5cu0022:\x5cu0022" + OtherToken + "\x5cu0022")]
    [InlineData("http://someone:" + OtherToken + "@host/")]
    [InlineData("{\"password\" : \"" + OtherToken + "\"}")]
    [InlineData("'token' => '" + OtherToken + "'")]
    [InlineData("[\"AccessToken\"]=> \"" + OtherToken + "\"")]
    [InlineData("<AccessToken>" + OtherToken + "</AccessToken>")]
    [InlineData("&#34;AccessToken&#34;:&#34;" + OtherToken + "&#34;")]
    [InlineData("&#x22;AccessToken&#x22;:&#x22;" + OtherToken + "&#x22;")]
    [InlineData("{\"tokens\":[\"" + OtherToken + "\"]}")]
    [InlineData("{\"tokens\":{\"a\":\"" + OtherToken + "\"}}")]
    [InlineData("{\"ChangePassword\":{\"CurrentPw\":\"a}" + OtherToken + "\",\"NewPw\":\"x\"}}")]
    [InlineData("{&quot;tokens&quot;:{&quot;access_token&quot;:&quot;a}" + OtherToken + "&quot;}}")]
    [InlineData("http:\\\\/\\\\/a:" + OtherToken + "@host")]
    [InlineData("http:\\/\\/a:" + OtherToken + "@host")]
    [InlineData("pwd=" + OtherToken)]
    [InlineData("{\"NewPw\":\"" + OtherToken + "\"}")]
    [InlineData("X-Api-Key: " + OtherToken)]
    [InlineData("http://x/a.strm?a=b&amp;sig=" + OtherToken)]
    [InlineData("http://a:" + OtherToken + "@x@host/")]
    public async Task TheBodyOfTheRemoteIsShownWithoutTokens(string echoed)
    {
        var handler = new StubHandler(_ => StubHandler.Json("before " + echoed + " after", HttpStatusCode.InternalServerError));

        var result = await ClientFor(handler).GetLibrariesAsync(_endpoint, _session, CancellationToken.None);

        var detail = result.Error!.ToString();
        Assert.Contains("before", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Token, detail, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherToken, detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pa55-w0rd-sentinel", "pa55-w0rd-sentinel")]
    [InlineData("a\"b\\c d&e", "a\\\"b\\\\c d&e")]
    [InlineData("a b&c=d+e", "a%20b%26c%3Dd%2Be")]
    [InlineData("a b&c=d+e", "a+b%26c%3dd%2be")]
    [InlineData("<pass>word", "\x5cu003Cpass\x5cu003Eword")]
    [InlineData("<pass>word", "&lt;pass&gt;word")]
    [InlineData("pass+word", "pass&#x2B;word")]
    [InlineData("pass/word", "pass\\/word")]
    [InlineData("a\tb-password", "a\tb-password")]
    public async Task ABodyThatHoldsThePasswordIsNotShown(string password, string echoed)
    {
        var handler = new StubHandler(_ => StubHandler.Json("sent " + echoed + " refused", HttpStatusCode.InternalServerError));
        var credentials = new RemoteCredentials { Username = "user", Password = password };

        var result = await ClientFor(handler).AuthenticateAsync(_endpoint, credentials, CancellationToken.None);

        Assert.DoesNotContain("sent ", result.Error!.ToString(), StringComparison.Ordinal);
        Assert.Contains("HTTP 500", result.Error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("jellyfin")]
    [InlineData("users")]
    [InlineData("remote")]
    [InlineData("http")]
    public async Task ACommonPasswordLeavesNoHoleInWhatThePluginWrites(string password)
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("no answer"));
        var credentials = new RemoteCredentials { Username = "user", Password = password };

        var result = await ClientFor(handler).AuthenticateAsync(_endpoint, credentials, CancellationToken.None);

        var detail = result.Error!.ToString();
        Assert.Contains("POST " + TestValues.Url + "/Users/AuthenticateByName", detail, StringComparison.Ordinal);
        Assert.Contains("System.Net.Http.HttpRequestException: no answer", detail, StringComparison.Ordinal);
        Assert.Contains("at Jellyfin.Plugin.JellyMesh.Remote.JellyfinRemoteClient.", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(ErrorText.Removed, detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExceptionMessageThatHoldsThePasswordIsNotShown()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("got pa55-w0rd-sentinel back"));
        var credentials = new RemoteCredentials { Username = "user", Password = "pa55-w0rd-sentinel" };

        var result = await ClientFor(handler).AuthenticateAsync(_endpoint, credentials, CancellationToken.None);

        Assert.DoesNotContain("pa55-w0rd-sentinel", result.Error!.ToString(), StringComparison.Ordinal);
        Assert.Contains("System.Net.Http.HttpRequestException: ", result.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenTooLongIsRefusedAndNotShown()
    {
        var token = new string('<', 8 * 1024 * 1024);
        var handler = new StubHandler(_ => StubHandler.Json("{\"AccessToken\":\"" + token + "\",\"User\":{\"Id\":\"u\",\"Policy\":{\"IsAdministrator\":false}}}"));

        var result = await ClientFor(handler).AuthenticateAsync(_endpoint, TestValues.Credentials, CancellationToken.None);

        Assert.Equal(RemoteFailure.InvalidResponse, result.Failure);
        Assert.DoesNotContain("<<<<", result.Error!.ToString(), StringComparison.Ordinal);
        Assert.Contains("[not shown", result.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABodyOfManyLinesIsCutAtItsLines()
    {
        var handler = new StubHandler(_ => StubHandler.Json(new string('\n', 60_000), HttpStatusCode.InternalServerError));

        var result = await ClientFor(handler).GetLibrariesAsync(_endpoint, _session, CancellationToken.None);

        Assert.InRange(result.Detail.Length, 1, 40_000);
        Assert.Contains("cut at 2000 lines", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(' ')]
    [InlineData('\t')]
    public async Task ABodyOfManySpacesIsCleanedInTime(char space)
    {
        var handler = new StubHandler(_ => StubHandler.Json("before token:" + new string(space, 64_000) + ",", HttpStatusCode.InternalServerError));

        var result = await ClientFor(handler).GetLibrariesAsync(_endpoint, _session, CancellationToken.None);

        Assert.Contains("before", result.Error!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tokens={")]
    [InlineData("tokens=[")]
    public void AnOpenBracketDoesNotTakeTheStackTrace(string open)
    {
        var error = ThrownWith("before " + open + "\n  \"a\": \"" + OtherToken + "\"\n  after");

        var text = ErrorText.Of(error);

        Assert.DoesNotContain(OtherToken, text, StringComparison.Ordinal);
        Assert.Contains("before", text, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrownWith), text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tokens: {\n    at first\n    a: \"X", "\"\n}")]
    [InlineData("tokens: [\n  --- one\n  \"X", "\"\n]")]
    [InlineData("tokens: {\n\tat first\n\ta: \"X", "\"\n}")]
    public async Task ABracketOfABodyIsRemovedPastALineLikeAStackTrace(string start, string end)
    {
        var handler = new StubHandler(_ => StubHandler.Json(start + OtherToken + end, HttpStatusCode.InternalServerError));

        var result = await ClientFor(handler).GetLibrariesAsync(_endpoint, _session, CancellationToken.None);

        Assert.DoesNotContain(OtherToken, result.Error!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("token", ' ')]
    [InlineData(":", '\\')]
    [InlineData("Bearer", ' ')]
    [InlineData("tokens={", '\n')]
    [InlineData("tokens=[", '\n')]
    public void AHostileTextIsCleanedInTime(string start, char repeated)
    {
        var text = ErrorText.Of(new InvalidOperationException("before " + start + new string(repeated, 64_000)));

        Assert.Contains("before", text, StringComparison.Ordinal);
    }

    [Fact]
    public void APasswordInAUrlAcrossTheCutIsNotShown()
    {
        var removed = " token=" + new string('y', 60_000);
        for (var at = 1; at < 40; at++)
        {
            var start = new string('x', (72 * 1024) - "System.InvalidOperationException: ".Length - removed.Length - at - "://u:".Length);

            var text = ErrorText.Of(new InvalidOperationException(start + removed[..^1] + " ://u:" + new string('P', 60) + "@host/"));

            Assert.DoesNotContain("PPPP", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ALongExceptionMessageIsCutBeforeItsSecretsAreRemoved()
    {
        var message = string.Concat(Enumerable.Repeat(TestValues.Token + " ", 4000)) + "the-very-end";
        Assert.True(message.Length > 80_000);
        var handler = new StubHandler(_ => throw new HttpRequestException(message));

        var result = await ClientFor(handler).GetLibrariesAsync(_endpoint, _session, CancellationToken.None);

        var detail = result.Error!.ToString();
        Assert.Contains(ErrorText.Removed, detail, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Token[..8], detail, StringComparison.Ordinal);
        Assert.DoesNotContain("the-very-end", detail, StringComparison.Ordinal);
        Assert.Contains("cut at 65536 characters", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecretCutByABodyThatStopsIsNotShown()
    {
        var part = TestValues.Token[..^2];
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StreamContent(new StoppingStream("before " + part)) });

        var result = await ClientFor(handler).GetLibrariesAsync(_endpoint, _session, CancellationToken.None);

        Assert.DoesNotContain(part, result.Error!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AShortPasswordHidesTheWholeBody()
    {
        var handler = new StubHandler(_ => StubHandler.Json("refused ab", HttpStatusCode.InternalServerError));
        var credentials = new RemoteCredentials { Username = "user", Password = "ab" };

        var result = await ClientFor(handler).AuthenticateAsync(_endpoint, credentials, CancellationToken.None);

        Assert.DoesNotContain("refused", result.Error!.ToString(), StringComparison.Ordinal);
        Assert.Contains("HTTP 500", result.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedLoginAnswerDoesNotShowTheTokenItHolds()
    {
        var handler = new StubHandler(_ => StubHandler.Json("{\"AccessToken\":\"" + TestValues.Token + "\",\"User\":null}"));

        var result = await ClientFor(handler).AuthenticateAsync(_endpoint, TestValues.Credentials, CancellationToken.None);

        Assert.Equal(RemoteFailure.InvalidResponse, result.Failure);
        Assert.DoesNotContain(TestValues.Token, result.Error!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBodyOfTheRemoteIsCutAt64Kilobytes()
    {
        var handler = new StubHandler(_ => StubHandler.Json(new string('x', 200_000) + "the-very-end", HttpStatusCode.InternalServerError));

        var result = await ClientFor(handler).GetLibrariesAsync(_endpoint, _session, CancellationToken.None);

        var detail = result.Error!.ToString();
        Assert.InRange(detail.Length, 60_000, 70_000);
        Assert.DoesNotContain("the-very-end", detail, StringComparison.Ordinal);
        Assert.Contains("cut at 65536 characters", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecretAcrossTheCutIsNotShown()
    {
        var head = new string('x', (64 * 1024) - 12);
        var handler = new StubHandler(_ => StubHandler.Json(head + TestValues.Token, HttpStatusCode.InternalServerError));

        var result = await ClientFor(handler).GetLibrariesAsync(_endpoint, _session, CancellationToken.None);

        Assert.DoesNotContain(TestValues.Token[..8], result.Error!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABodyThatStopsArrivingStillGivesTheStatus()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StreamContent(new SilentStream()) });

        var result = await ClientFor(handler, TimeSpan.FromMilliseconds(200)).AuthenticateAsync(_endpoint, TestValues.Credentials, CancellationToken.None);

        Assert.Equal(RemoteFailure.ServerError, result.Failure);
        Assert.Equal(502, result.Status);
        Assert.Contains("HTTP 502", result.Error!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgedLinesAndUnsafeCharactersStayInsideTheDetail()
    {
        var handler = new StubHandler(_ => StubHandler.Json("first\r\n[ERR] forged line\x202E\x0007", HttpStatusCode.InternalServerError));

        var result = await ClientFor(handler).GetLibrariesAsync(_endpoint, _session, CancellationToken.None);

        var detail = result.Error!.ToString();
        Assert.DoesNotContain("\n[ERR] forged line", detail, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', detail);
        Assert.DoesNotContain('\x202E', detail);
        Assert.DoesNotContain('\x0007', detail);
        Assert.Contains("[ERR] forged line", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlaybackCallSaysWhatWasAskedAndWhatTheRemoteAnswered()
    {
        var handler = new StubHandler(_ => StubHandler.Json("No such item " + TestValues.Token, HttpStatusCode.NotFound));
        var client = new RemotePlaybackClient(new StubHttpClientFactory(handler));

        var stream = await client.OpenStreamAsync(_endpoint, _session, new RemoteStreamRequest(HttpMethod.Get, "/Videos/1/stream?ApiKey=" + OtherToken, null), CancellationToken.None);
        var stop = await client.StopEncodingAsync(_endpoint, _session, "play", CancellationToken.None);

        var detail = stream.Error!.ToString();
        Assert.Equal(404, stream.Status);
        Assert.Contains("Step: OpenStream", detail, StringComparison.Ordinal);
        Assert.Contains("GET " + TestValues.Url + "/Videos/1/stream", detail, StringComparison.Ordinal);
        Assert.Contains("No such item", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Token, detail, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherToken, detail, StringComparison.Ordinal);
        Assert.Contains("Step: StopEncoding", stop.Error!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlaybackPathThatIsRefusedIsNamed()
    {
        var handler = new StubHandler(_ => StubHandler.Status(HttpStatusCode.OK));
        var client = new RemotePlaybackClient(new StubHttpClientFactory(handler));

        var result = await client.ReadPlaylistAsync(_endpoint, _session, "//elsewhere/x.m3u8", null, CancellationToken.None);

        Assert.Equal(RemoteFailure.InvalidResponse, result.Failure);
        Assert.Empty(handler.Requests);
        Assert.Contains("//elsewhere/x.m3u8", result.Error!.ToString(), StringComparison.Ordinal);
    }

    private static InvalidOperationException ThrownWith(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    private static JellyfinRemoteClient ClientFor(StubHandler handler, TimeSpan? limit = null) => new(new StubHttpClientFactory(handler))
    {
        Delay = (_, _) => Task.CompletedTask,
        RequestLimit = limit ?? JellyfinRemoteClient.RequestTimeout
    };

    // A body that breaks off after a text.
    private sealed class StoppingStream(string text) : MemoryStream(System.Text.Encoding.UTF8.GetBytes(text))
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            return read > 0 ? read : throw new IOException("The connection broke");
        }
    }

    // A body that never arrives.
    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
