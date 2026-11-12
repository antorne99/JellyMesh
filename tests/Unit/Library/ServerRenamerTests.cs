using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class ServerRenamerTests : IDisposable
{
    private static readonly Guid _server = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000001");

    private readonly Mock<IRemoteServerRegistry> _registry = new();
    private readonly Mock<IServerStubRenamer> _stubs = new();
    private readonly Mock<IServerLibraryManager> _libraries = new();
    private readonly Mock<ILibraryScans> _scans = new();
    private readonly Mock<IVersionLinker> _linker = new();
    private readonly RetiredLibraryIds _retired = new();
    private readonly CapturingLogger<ServerRenamer> _logger = new();
    private readonly List<string> _steps = [];
    private readonly ServerRenamer _renamer;
    private bool _pending;

    public ServerRenamerTests()
    {
        _registry.Setup(registry => registry.GetServers()).Returns([new RemoteServerInfo(_server, "Casa", "http://casa", RemoteMode.Standard, 0, [], null, null)]);
        _registry.Setup(registry => registry.Rename(_server, It.IsAny<string>())).Callback((Guid _, string name) => _steps.Add("name " + name)).Returns(true);
        _stubs.Setup(stubs => stubs.IsPending(_server)).Returns(() => _pending);
        _stubs.Setup(stubs => stubs.RelabelAsync(_server, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, string name, CancellationToken _) => _steps.Add("stubs " + name))
            .ReturnsAsync(2);
        _stubs.Setup(stubs => stubs.FinishAsync(It.IsAny<CancellationToken>())).Callback(() => _steps.Add("finish")).Returns(Task.CompletedTask);
        _libraries.Setup(libraries => libraries.RenameAsync(_server, It.IsAny<CancellationToken>())).Callback(() => _steps.Add("libraries")).ReturnsAsync(true);
        _libraries.Setup(libraries => libraries.GetState(_server)).Returns(new AutomaticLibrariesState(
            true,
            false,
            [new AutomaticLibraryStatus(ServerLibraryKind.Movies, "Casa - Film"), new AutomaticLibraryStatus(ServerLibraryKind.Series, "Casa - Serie")],
            [],
            AutomaticLibrariesError.None));
        _scans.Setup(scans => scans.QueueAndWaitAsync(It.IsAny<CancellationToken>())).Callback(() => _steps.Add("scan")).Returns(Task.CompletedTask);
        _linker.Setup(linker => linker.LinkAsync(false, It.IsAny<CancellationToken>())).Callback(() => _steps.Add("link")).Returns(Task.CompletedTask);
        _renamer = new ServerRenamer(_registry.Object, _stubs.Object, _libraries.Object, _scans.Object, _linker.Object, _retired, _logger);
    }

    public void Dispose() => _renamer.Dispose();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankName_IsRefused(string? name)
    {
        var answer = _renamer.Rename(_server, name, true);

        Assert.Equal(ServerRenameOutcome.InvalidName, answer.Outcome);
        _registry.Verify(registry => registry.Rename(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void AnUnknownServer_IsRefused()
    {
        var answer = _renamer.Rename(Guid.NewGuid(), "Mare", true);

        Assert.Equal(ServerRenameOutcome.UnknownServer, answer.Outcome);
    }

    [Fact]
    public void WithoutAConfirmation_NothingChanges_AndTheLibrariesToRenameAreListed()
    {
        var answer = _renamer.Rename(_server, "Mare", false);

        Assert.Equal(ServerRenameOutcome.ConfirmationRequired, answer.Outcome);
        Assert.Equal(["Casa - Film", "Casa - Serie"], answer.Libraries);
        Assert.Empty(_steps);
    }

    [Fact]
    public void WhileStubsOfTheServerWaitForAScan_ARenameIsRefused()
    {
        _pending = true;

        var answer = _renamer.Rename(_server, "Mare", true);

        Assert.Equal(ServerRenameOutcome.Busy, answer.Outcome);
        Assert.True(_renamer.GetState(_server).Pending);
        Assert.Empty(_steps);
    }

    [Fact]
    public async Task TheServer_ThenItsStubs_ThenItsLibraries_AreRenamed_ThenAScanRuns_AndTheVersionsAreLinked()
    {
        var answer = _renamer.Rename(_server, " Mare ", true);
        await answer.Job;

        Assert.Equal(ServerRenameOutcome.Started, answer.Outcome);
        Assert.Equal(["name Mare", "stubs Mare", "libraries", "scan", "finish", "link"], _steps);
        Assert.Equal(new ServerRenameState(false, false, false), _renamer.GetState(_server));
    }

    [Fact]
    public async Task WhileTheOldItemsAreStillThere_ASecondScanRemovesThem_AndNoThirdRuns()
    {
        _stubs.Setup(stubs => stubs.FinishAsync(It.IsAny<CancellationToken>())).Callback(() =>
        {
            _steps.Add("finish");
            _pending = true;
        }).Returns(Task.CompletedTask);

        await _renamer.Rename(_server, "Mare", true).Job;

        Assert.Equal(["name Mare", "stubs Mare", "libraries", "scan", "finish", "link", "scan", "finish", "link"], _steps);
        Assert.True(_renamer.GetState(_server).Pending);
    }

    [Fact]
    public async Task WhenTheLibraryRenameThrows_TheScanStillRuns_AndTheFailureIsLoggedByItsTypeOnly()
    {
        _libraries.Setup(libraries => libraries.RenameAsync(_server, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("/config/root/default/Casa - Film"));

        await _renamer.Rename(_server, "Mare", true).Job;

        Assert.Equal(["name Mare", "stubs Mare", "scan"], _steps);
        Assert.True(_renamer.GetState(_server).Failed);
        Assert.Contains(_logger.Lines, line => line.Contains(nameof(IOException), StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Lines, line => line.Contains("Casa", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenALibraryCannotBeRenamed_TheStubsStillFinish_AndThePageShowsTheLibraryError()
    {
        _libraries.Setup(libraries => libraries.RenameAsync(_server, It.IsAny<CancellationToken>())).Callback(() => _steps.Add("libraries")).ReturnsAsync(false);

        await _renamer.Rename(_server, "Mare", true).Job;

        Assert.Equal(["name Mare", "stubs Mare", "libraries", "scan", "finish", "link"], _steps);
        Assert.False(_renamer.GetState(_server).Failed);
    }

    [Fact]
    public async Task WhenTheStubsCannotMove_NothingElseRuns_AndTheRenameFailed()
    {
        _stubs.Setup(stubs => stubs.RelabelAsync(_server, It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk"));

        await _renamer.Rename(_server, "Mare", true).Job;

        Assert.Equal(["name Mare"], _steps);
        Assert.True(_renamer.GetState(_server).Failed);
    }

    [Fact]
    public async Task ARetryAfterAFailure_ClearsTheFailure()
    {
        _stubs.SetupSequence(stubs => stubs.RelabelAsync(_server, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk"))
            .ReturnsAsync(0);
        await _renamer.Rename(_server, "Mare", true).Job;

        await _renamer.Rename(_server, "Mare", true).Job;

        Assert.False(_renamer.GetState(_server).Failed);
    }

    [Fact]
    public async Task WhileARenameRuns_AnotherIsRefused_AndTheStateIsBusy()
    {
        var release = new TaskCompletionSource();
        _libraries.Setup(libraries => libraries.RenameAsync(_server, It.IsAny<CancellationToken>())).Returns(async () =>
        {
            await release.Task;
            return true;
        });
        var first = _renamer.Rename(_server, "Mare", true);

        var second = _renamer.Rename(_server, "Monte", true);

        Assert.Equal(ServerRenameOutcome.Busy, second.Outcome);
        Assert.True(_renamer.GetState(_server).Busy);
        release.SetResult();
        await first.Job;
        Assert.False(_renamer.GetState(_server).Busy);
    }

    [Fact]
    public void ARestartIsNeeded_OnlyOnceALibraryKeepsAnOldId()
    {
        Assert.False(_renamer.NeedsRestart);

        _retired.Retire(Guid.NewGuid());

        Assert.True(_renamer.NeedsRestart);
    }
}
