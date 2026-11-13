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
    private readonly Mock<IFederatedFolderLinkChecker> _links = new();
    private readonly RetiredLibraryIds _retired = new();
    private readonly CapturingLogger<ServerRenamer> _logger = new();
    private readonly InMemoryConfigurationStore _store = new();
    private readonly List<string> _steps = [];
    private readonly List<ServerRenamer> _made = [];
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
        _stubs.Setup(stubs => stubs.RelabelInPlaceAsync(_server, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, string name, CancellationToken _) => _steps.Add("stubs in place " + name))
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
        _stubs.Setup(stubs => stubs.CancelAsync(_server, It.IsAny<CancellationToken>())).Callback(() => _steps.Add("cancel")).ReturnsAsync(true);
        _links.Setup(links => links.Check(It.IsAny<string>(), It.IsAny<IReadOnlyList<Guid>>())).Returns(Report(FederatedPathLink.Linked, FederatedPathLink.NotLinked));
        _store.Configuration.Servers.Add(new RemoteServerConfig { Id = _server, Name = "Casa" });
        _renamer = NewRenamer();
    }

    public void Dispose()
    {
        foreach (var renamer in _made)
        {
            renamer.Dispose();
        }
    }

    // A renamer over the same saved configuration, as after a restart.
    private ServerRenamer NewRenamer()
    {
        var renamer = new ServerRenamer(_registry.Object, _stubs.Object, _libraries.Object, _scans.Object, _linker.Object, _retired, _links.Object, _store, _logger);
        _made.Add(renamer);
        return renamer;
    }

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
    public async Task WhenALibraryCannotBeRenamed_TheStubsStillFinish_AndTheRenameStaysUnfinished()
    {
        _libraries.Setup(libraries => libraries.RenameAsync(_server, It.IsAny<CancellationToken>())).Callback(() => _steps.Add("libraries")).ReturnsAsync(false);

        await _renamer.Rename(_server, "Mare", true).Job;

        Assert.Equal(["name Mare", "stubs Mare", "libraries", "scan", "finish", "link"], _steps);
        Assert.True(_renamer.GetState(_server).Failed);
    }

    [Fact]
    public async Task AFailedRename_IsStillShownAfterARestart()
    {
        _stubs.Setup(stubs => stubs.RelabelAsync(_server, It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk"));

        await _renamer.Rename(_server, "Mare", true).Job;

        Assert.True(NewRenamer().GetState(_server).Failed);
    }

    [Fact]
    public async Task ARenameStoppedByTheShutdown_IsShownAfterARestart()
    {
        var entered = new TaskCompletionSource();
        _libraries.Setup(libraries => libraries.RenameAsync(_server, It.IsAny<CancellationToken>())).Returns(async (Guid _, CancellationToken token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return true;
        });
        var renamer = NewRenamer();
        var job = renamer.Rename(_server, "Mare", true).Job;
        await entered.Task;

        renamer.Dispose();
        _made.Remove(renamer);
        await job;

        Assert.True(_store.Configuration.Servers[0].RenameUnfinished);
        Assert.True(NewRenamer().GetState(_server).Failed);
    }

    [Fact]
    public async Task ACompletedRename_ClearsTheSavedFailure()
    {
        _store.Configuration.Servers[0].RenameUnfinished = true;
        Assert.True(_renamer.GetState(_server).Failed);

        await _renamer.Rename(_server, "Mare", true).Job;

        Assert.False(_store.Configuration.Servers[0].RenameUnfinished);
        Assert.False(_renamer.GetState(_server).Failed);
    }

    [Fact]
    public async Task WhileARenameRuns_ItIsNotShownAsFailed()
    {
        var release = new TaskCompletionSource();
        _libraries.Setup(libraries => libraries.RenameAsync(_server, It.IsAny<CancellationToken>())).Returns(async () =>
        {
            await release.Task;
            return true;
        });
        var job = _renamer.Rename(_server, "Mare", true).Job;

        Assert.Equal(new ServerRenameState(true, false, false), _renamer.GetState(_server));
        release.SetResult();
        await job;
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
    [Fact]
    public async Task WithTheServerFolderInNoLibrary_TheStubFilesAreRenamedInPlace_AndNothingWaitsForAScan()
    {
        _store.Configuration.FederatedFolderPath = "/federated";
        _links.Setup(links => links.Check("/federated", new[] { _server })).Returns(Report(FederatedPathLink.NotLinked, FederatedPathLink.NotLinked));

        await _renamer.Rename(_server, "Mare", true).Job;

        Assert.Equal(["name Mare", "stubs in place Mare", "libraries", "scan"], _steps);
        Assert.Equal(new ServerRenameState(false, false, false), _renamer.GetState(_server));
    }

    [Fact]
    public async Task WithTheServerFolderInNoLibrary_TheScanStillRuns_WhenTheLibraryRenameThrows()
    {
        _store.Configuration.FederatedFolderPath = "/federated";
        _links.Setup(links => links.Check("/federated", new[] { _server })).Returns(Report(FederatedPathLink.NotLinked, FederatedPathLink.NotLinked));
        _libraries.Setup(libraries => libraries.RenameAsync(_server, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk"));

        await _renamer.Rename(_server, "Mare", true).Job;

        Assert.Equal(["name Mare", "stubs in place Mare", "scan"], _steps);
        Assert.True(_renamer.GetState(_server).Failed);
    }

    [Fact]
    public async Task WithTheServerFolderInNoLibrary_WhenTheStubFilesCannotMove_NothingElseRuns_AndTheRenameFailed()
    {
        _store.Configuration.FederatedFolderPath = "/federated";
        _links.Setup(links => links.Check("/federated", new[] { _server })).Returns(Report(FederatedPathLink.NotLinked, FederatedPathLink.NotLinked));
        _stubs.Setup(stubs => stubs.RelabelInPlaceAsync(_server, It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk"));

        await _renamer.Rename(_server, "Mare", true).Job;

        Assert.Equal(["name Mare"], _steps);
        Assert.True(_renamer.GetState(_server).Failed);
    }

    [Theory]
    [InlineData(FederatedPathLink.Linked, FederatedPathLink.NotLinked)]
    [InlineData(FederatedPathLink.NotLinked, FederatedPathLink.DifferentCase)]
    [InlineData(FederatedPathLink.NotLinked, FederatedPathLink.WrongLibraryType)]
    public async Task WithTheServerFolderInALibrary_TheStubsMove(FederatedPathLink movies, FederatedPathLink series)
    {
        _store.Configuration.FederatedFolderPath = "/federated";
        _links.Setup(links => links.Check("/federated", new[] { _server })).Returns(Report(movies, series));

        await _renamer.Rename(_server, "Mare", true).Job;

        Assert.Equal(["name Mare", "stubs Mare", "libraries", "scan", "finish", "link"], _steps);
    }

    [Fact]
    public async Task WithALibraryOverTheFederatedFolder_TheStubsMove()
    {
        _store.Configuration.FederatedFolderPath = "/federated";
        _links.Setup(links => links.Check("/federated", new[] { _server }))
            .Returns(Report(FederatedPathLink.NotLinked, FederatedPathLink.NotLinked) with { Covering = ["Tutto"] });

        await _renamer.Rename(_server, "Mare", true).Job;

        Assert.Contains("stubs Mare", _steps);
    }

    [Fact]
    public async Task WhenTheLibrariesCannotBeRead_TheStubsMove()
    {
        _store.Configuration.FederatedFolderPath = "/federated";
        _links.Setup(links => links.Check("/federated", new[] { _server })).Returns(FederatedFolderLinkReport.NotChecked);

        await _renamer.Rename(_server, "Mare", true).Job;

        Assert.Contains("stubs Mare", _steps);
    }

    [Fact]
    public async Task CancellingDropsThePendingMovesOfTheServer()
    {
        Assert.Equal(ServerRenameOutcome.Cancelled, await _renamer.CancelPendingAsync(_server));

        Assert.Equal(["cancel"], _steps);
    }

    [Fact]
    public async Task WhenAnOldFileCannotBeDeleted_TheCancelFails()
    {
        _stubs.Setup(stubs => stubs.CancelAsync(_server, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        Assert.Equal(ServerRenameOutcome.CancelFailed, await _renamer.CancelPendingAsync(_server));
    }

    [Fact]
    public async Task WhenCancellingThrows_TheCancelFails_AndTheErrorIsLoggedByItsTypeOnly()
    {
        _stubs.Setup(stubs => stubs.CancelAsync(_server, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("/secret/path"));

        Assert.Equal(ServerRenameOutcome.CancelFailed, await _renamer.CancelPendingAsync(_server));
        Assert.DoesNotContain(_logger.Lines, line => line.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellingAnUnknownServer_IsRefused()
    {
        Assert.Equal(ServerRenameOutcome.UnknownServer, await _renamer.CancelPendingAsync(Guid.NewGuid()));

        Assert.Empty(_steps);
    }

    [Fact]
    public async Task WhileARenameRuns_ACancelIsRefused()
    {
        var release = new TaskCompletionSource();
        _libraries.Setup(libraries => libraries.RenameAsync(_server, It.IsAny<CancellationToken>())).Returns(async () =>
        {
            await release.Task;
            return true;
        });
        var job = _renamer.Rename(_server, "Mare", true).Job;

        Assert.Equal(ServerRenameOutcome.Busy, await _renamer.CancelPendingAsync(_server));
        release.SetResult();
        await job;
        Assert.DoesNotContain("cancel", _steps);
    }

    [Fact]
    public async Task WhileACancelRuns_ARenameIsRefused()
    {
        var release = new TaskCompletionSource<bool>();
        _stubs.Setup(stubs => stubs.CancelAsync(_server, It.IsAny<CancellationToken>())).Returns(release.Task);
        var cancel = _renamer.CancelPendingAsync(_server);

        Assert.Equal(ServerRenameOutcome.Busy, _renamer.Rename(_server, "Mare", true).Outcome);
        release.SetResult(true);
        Assert.Equal(ServerRenameOutcome.Cancelled, await cancel);
    }

    private static FederatedFolderLinkReport Report(FederatedPathLink movies, FederatedPathLink series) => new(
        [new ServerFolderLink(_server, "/m", "/s", new FederatedPathStatus(movies, []), new FederatedPathStatus(series, []), [], [], [])],
        [],
        false);
}
