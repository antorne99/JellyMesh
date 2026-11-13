using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Sync;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

// Q386: a periodic sync with nothing changed skips the materialization and the links; the nightly run never does, and
// only a run that completed with nothing left over is compared with.
public sealed class UnchangedSyncTests : IDisposable
{
    private const long Generation = 7;

    private readonly SyncFixture _fixture = new();
    private readonly Mock<ILibraryMaterializer> _materializer = new();
    private readonly Mock<IVersionLinker> _linker = new();
    private readonly FakeGate _gate = new();
    private readonly List<string> _calls = [];
    private MaterializationReport _report = new(DateTimeOffset.UnixEpoch, true, 0, 0, 0, 0, 0, []) { RegistryGeneration = Generation };

    public UnchangedSyncTests()
    {
        _materializer.Setup(materializer => materializer.MaterializeAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((bool thorough, CancellationToken _) => _calls.Add("materialize " + thorough))
            .ReturnsAsync(() => _report);
        _linker.Setup(linker => linker.LinkAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((bool thorough, CancellationToken _) => _calls.Add("link " + thorough))
            .Returns(Task.CompletedTask);
        _linker.Setup(linker => linker.LastRunFailed).Returns(false);
        _linker.Setup(linker => linker.LastFullRun).Returns(new VersionLinkRun(Generation, Generation + 1));
        _gate.Calls = _calls;
    }

    public static TheoryData<string> Leftovers => new()
    {
        "a stub failed",
        "a local title awaits its refresh",
        "an image is pending",
        "a scan was queued",
        "no federated folder",
        "the materialization failed",
        "the links failed",
        "a link run failed",
        "someone saved the registry in between",
        "no full link run"
    };

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task AnIncrementalSyncWithNothingChangedSkipsMaterializingAndLinking()
    {
        _gate.Unchanged = true;
        using var sync = NewSync();

        await sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(["capture"], _calls);
        Assert.Contains(_fixture.Logger.Lines, line => line.StartsWith("Nothing changed since the last sync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheNightlyRunNeverSkips()
    {
        _gate.Unchanged = true;
        using var sync = NewSync();

        await sync.RunAsync(CancellationToken.None);

        Assert.Equal(["capture", "forget", "materialize True", "link True", "remember 8"], _calls);
    }

    [Fact]
    public async Task ARunThatCompletedIsRememberedWithTheRegistryTheLinksLeft()
    {
        using var sync = NewSync();

        await sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(["capture", "forget", "materialize False", "link False", "remember 8"], _calls);
    }

    [Theory]
    [MemberData(nameof(Leftovers))]
    public async Task ARunThatLeftSomethingIsNotRemembered(string leftover)
    {
        switch (leftover)
        {
            case "a stub failed":
                _report = _report with { Failed = 1 };
                break;
            case "a local title awaits its refresh":
                _report = _report with { AwaitingRefresh = 1 };
                break;
            case "an image is pending":
                _report = _report with { ImagesPending = 1 };
                break;
            case "a scan was queued":
                _report = _report with { ScanQueued = true };
                break;
            case "no federated folder":
                _report = _report with { IsFolderSet = false };
                break;
            case "the materialization failed":
                _materializer.Setup(materializer => materializer.MaterializeAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
                break;
            case "the links failed":
                _linker.Setup(linker => linker.LinkAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
                break;
            case "a link run failed":
                _linker.Setup(linker => linker.LastRunFailed).Returns(true);
                break;
            case "someone saved the registry in between":
                _linker.Setup(linker => linker.LastFullRun).Returns(new VersionLinkRun(Generation + 1, Generation + 1));
                break;
            case "no full link run":
                _linker.Setup(linker => linker.LastFullRun).Returns((VersionLinkRun?)null);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(leftover), leftover, null);
        }

        using var sync = NewSync();

        await sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Contains("forget", _calls);
        Assert.DoesNotContain(_calls, call => call.StartsWith("remember", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACancelledRunIsNotRemembered()
    {
        var migration = new Mock<ILayoutMigration>();
        migration.Setup(run => run.RunAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        using var sync = NewSync(migration.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() => sync.RunIncrementalAsync(CancellationToken.None));

        Assert.Equal(["capture", "forget", "materialize False", "link False"], _calls);
    }

    [Fact]
    public async Task TheCleanupAfterARemovalNeverSkipsAndForgetsTheLastRun()
    {
        _gate.Unchanged = true;
        using var sync = NewSync();

        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);

        Assert.Equal(["forget", "materialize False", "link False"], _calls);
    }

    private CatalogSyncService NewSync(ILayoutMigration? migration = null) => new(
        _fixture.Registry,
        _fixture.Reader,
        _fixture.Index,
        _fixture.Vault,
        _fixture.Clock,
        _fixture.Logger,
        _materializer.Object,
        _fixture.Stubs,
        linker: _linker.Object,
        migration: migration,
        gate: _gate);

    private sealed class FakeGate : IUnchangedSyncGate
    {
        public List<string> Calls { get; set; } = [];

        public bool Unchanged { get; set; }

        public SyncInputs Capture()
        {
            Calls.Add("capture");
            return new SyncInputs([], "inputs", 0, 0);
        }

        public bool IsUnchanged(SyncInputs inputs) => Unchanged;

        public void Remember(SyncInputs inputs) => Calls.Add("remember " + inputs.RegistryGeneration);

        public void Forget() => Calls.Add("forget");
    }
}
