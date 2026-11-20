using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

/// <summary>Decision 17: the endpoint of a <c>.strm</c> wants both the loopback socket and the signature.</summary>
public sealed class StrmControllerTests : IDisposable
{
    private const string StubId = "stub";

    private readonly string _folder = Directory.CreateTempSubdirectory("jellymesh-strm").FullName;
    private readonly CoordinatorFixture _fixture = new();
    private readonly Mock<IStubRegistry> _stubs = new();
    private readonly StrmSigner _signer;
    private readonly CapturingLogger<StrmController> _logger = new();

    public StrmControllerTests()
    {
        _signer = new StrmSigner(_folder, new CapturingLogger<StrmSigner>());
        _stubs.Setup(stubs => stubs.Find(StubId)).Returns(PlaybackValues.Stub(PlaybackValues.Source()));
    }

    public void Dispose() => Directory.Delete(_folder, true);

    private async Task<int> CallAsync(string stubId, string? signature, string address = "127.0.0.1", string? header = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Response.Body = new MemoryStream();
        if (header is not null)
        {
            context.Request.Headers[header] = "203.0.113.9";
        }

        var controller = new StrmController(_stubs.Object, _fixture.Coordinator, _signer, _logger)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = await controller.GetFile(stubId, signature, CancellationToken.None);
        return result is StatusCodeResult status ? status.StatusCode : context.Response.StatusCode;
    }

    [Fact]
    public async Task The_server_itself_with_the_signature_reads_the_file()
    {
        Assert.Equal(StatusCodes.Status200OK, await CallAsync(StubId, _signer.Sign(StubId)));
        Assert.Single(_fixture.Client.Opened);
    }

    [Theory]
    [InlineData("172.17.0.1", null)]
    [InlineData("198.51.100.9", null)]
    [InlineData("127.0.0.1", "X-Forwarded-For")]
    [InlineData("127.0.0.1", "Forwarded")]
    [InlineData("127.0.0.1", "X-Original-Host")]
    public async Task A_leaked_signature_grants_nothing_off_the_host(string address, string? header)
    {
        Assert.Equal(StatusCodes.Status403Forbidden, await CallAsync(StubId, _signer.Sign(StubId), address, header));
        Assert.Empty(_fixture.Client.Opened);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0000")]
    public async Task Loopback_alone_is_not_enough(string? signature)
    {
        Assert.Equal(StatusCodes.Status403Forbidden, await CallAsync(StubId, signature));
        Assert.Empty(_fixture.Client.Opened);
    }

    [Fact]
    public async Task The_signature_of_another_stub_is_refused()
    {
        Assert.Equal(StatusCodes.Status403Forbidden, await CallAsync(StubId, _signer.Sign("another-stub")));
    }

    [Fact]
    public async Task A_stub_that_does_not_exist_is_hidden_behind_the_signature()
    {
        Assert.Equal(StatusCodes.Status403Forbidden, await CallAsync("unknown", "0000"));
        Assert.Equal(StatusCodes.Status404NotFound, await CallAsync("unknown", _signer.Sign("unknown")));
        Assert.Contains(_logger.Lines, line => line.Contains("names a stub that no longer exists", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_no_source_opens_is_logged_with_the_failure_and_no_remote_value()
    {
        var fixtureLogger = new CapturingLogger<PlaybackCoordinator>();
        var fixture = new CoordinatorFixture(logger: fixtureLogger);
        fixture.Client.Open = (_, _) => Task.FromResult(Jellyfin.Plugin.JellyMesh.Remote.RemoteResult.FailWithStatus<Jellyfin.Plugin.JellyMesh.Remote.RemoteContent>(Jellyfin.Plugin.JellyMesh.Remote.RemoteFailure.ServerError, 502));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        var controller = new StrmController(_stubs.Object, fixture.Coordinator, _signer, _logger)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = await controller.GetFile(StubId, _signer.Sign(StubId), CancellationToken.None);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Contains(_logger.Lines, line => line.Contains("No source opened the file of stub stub", StringComparison.Ordinal));
        Assert.Contains(fixtureLogger.Lines, line => line.Contains("did not open: ServerError (HTTP 502)", StringComparison.Ordinal));
        PlaybackLogAssert.NoRemoteValue(_logger.Lines);
        PlaybackLogAssert.NoRemoteValue(fixtureLogger.Lines);
    }
}
