using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Tests.Playback;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Security;

/// <summary>
/// What each failure leaves in the log. Exception text is the trap: a transport error names the address, and a test on
/// the message format alone would pass with the exception attached.
/// </summary>
public class LogLeakTests
{
    private const string SecretText = "failed for http://" + TestValues.Host + ":8096/x?api_key=" + TestValues.Token;

    private static readonly string[] _secrets = [TestValues.Token, TestValues.Password, TestValues.Host, "Authorization", "api_key"];

    private static void AssertClean(IEnumerable<string> lines)
    {
        foreach (var secret in _secrets)
        {
            Assert.DoesNotContain(lines, line => line.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static StubHandler Failing(Exception exception, string? path = null)
    {
        return new StubHandler(request => request.RequestUri!.AbsolutePath == "/Users/AuthenticateByName" && path is null
            ? StubHandler.Json(TestValues.LoginJson)
            : throw exception);
    }

    public static TheoryData<int> Failures => new() { 0, 1, 2, 3, 4 };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task No_transport_failure_reaches_the_log_with_its_text(int failure)
    {
        Exception exception = failure switch
        {
            0 => new HttpRequestException(SecretText),
            1 => new System.IO.IOException(SecretText),
            2 => new HttpRequestException(HttpRequestError.ConfigurationLimitExceeded, SecretText),
            3 => new TaskCanceledException(SecretText),
            _ => new HttpIOException(HttpRequestError.ResponseEnded, SecretText)
        };
        using var fixture = new RegistryFixture(Failing(exception));
        var server = await fixture.AddServerAsync();

        var result = await fixture.Registry.GetLibrariesAsync(server.Id, CancellationToken.None);
        var capabilities = await fixture.Registry.RefreshCapabilitiesAsync(server.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(capabilities);
        AssertClean(fixture.Logger.Lines);
    }

    [Fact]
    public async Task A_refused_login_says_only_that()
    {
        using var fixture = new RegistryFixture(new StubHandler(_ => StubHandler.Status(HttpStatusCode.Unauthorized)));

        await fixture.Registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None);

        Assert.NotEmpty(fixture.Logger.Lines);
        AssertClean(fixture.Logger.Lines);
    }

    [Fact]
    public async Task A_failed_close_logs_the_kind_of_error_only()
    {
        var logger = new CapturingLogger<PlaybackSweeper>();
        var sessions = new Mock<ISessionManager>();
        var fixture = new CoordinatorFixture();
        await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));
        var session = Assert.Single(fixture.Sessions.All);
        fixture.Client.OnStop = () => throw new HttpRequestException(SecretText);
        using var sweeper = new PlaybackSweeper(fixture.Coordinator, sessions.Object, TimeProvider.System, logger);
        using var stop = new CancellationTokenSource();
        await sweeper.StartAsync(stop.Token);

        // The service subscribes on a thread of its own, so the event is raised until somebody hears it.
        for (var waited = 0; waited < 500 && logger.Lines.Count == 0; waited++)
        {
            sessions.Raise(manager => manager.PlaybackStopped += null, new PlaybackStopEventArgs { PlaySessionId = session.Id, Users = [new Jellyfin.Database.Implementations.Entities.User("viewer", "auth", "reset") { Id = session.Identity.UserId }] });
            await Task.Delay(10);
        }

        await stop.CancelAsync();
        await sweeper.StopAsync(CancellationToken.None);

        Assert.Contains(logger.Lines, line => line.Contains(nameof(HttpRequestException), StringComparison.Ordinal));
        AssertClean(logger.Lines);
    }

    [Fact]
    public async Task A_refusal_that_cannot_be_sent_logs_the_kind_of_error_only()
    {
        var logger = new CapturingLogger<PlaybackRefusal>();
        var sessions = new Mock<ISessionManager>();
        sessions.Setup(manager => manager.SendMessageToUserDeviceSessions(It.IsAny<string>(), It.IsAny<SessionMessageType>(), It.IsAny<GeneralCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(SecretText));
        var feature = new ResponseCompletion();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(feature);

        new PlaybackRefusal(sessions.Object, logger).Refuse(context, "device");
        await feature.CompleteAsync();

        Assert.Contains(logger.Lines, line => line.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        AssertClean(logger.Lines);
    }

    private sealed class ResponseCompletion : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _completed = [];

        public int StatusCode { get; set; } = 200;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public System.IO.Stream Body { get; set; } = System.IO.Stream.Null;

        public bool HasStarted => false;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state) => _completed.Add((callback, state));

        public Task CompleteAsync() => Task.WhenAll(_completed.Select(entry => entry.Callback(entry.State)));
    }
}
