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

public sealed class VersionLinkingSyncTests : IDisposable
{
    private readonly SyncFixture _fixture = new();
    private readonly Mock<ILibraryMaterializer> _materializer = new();
    private readonly Mock<IVersionLinker> _linker = new();
    private readonly List<string> _calls = [];
    private readonly CatalogSyncService _sync;

    public VersionLinkingSyncTests()
    {
        _materializer.Setup(materializer => materializer.MaterializeAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((bool thorough, CancellationToken _) => _calls.Add("materialize " + thorough))
            .ReturnsAsync(new MaterializationReport(DateTimeOffset.UnixEpoch, true, 0, 0, 0, 0, 0, []));
        _linker.Setup(linker => linker.LinkAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((bool thorough, CancellationToken _) => _calls.Add("link " + thorough))
            .Returns(Task.CompletedTask);
        _sync = NewSync(null);
    }

    public void Dispose()
    {
        _sync.Dispose();
        _fixture.Dispose();
    }

    [Fact]
    public async Task TheIncrementalSyncLinksAfterMaterializing()
    {
        await _sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(["materialize False", "link False"], _calls);
    }

    [Fact]
    public async Task TheReconciliationLinksThoroughly()
    {
        await _sync.RunAsync(CancellationToken.None);

        Assert.Equal(["materialize True", "link True"], _calls);
    }

    [Fact]
    public async Task AFailedMaterializationStillLinks()
    {
        _materializer.Setup(materializer => materializer.MaterializeAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());

        await _sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(["link False"], _calls);
    }

    [Fact]
    public async Task TheSyncMigratesTheOldStubsAfterLinking()
    {
        var migration = new Mock<ILayoutMigration>();
        migration.Setup(run => run.RunAsync(It.IsAny<CancellationToken>())).Callback(() => _calls.Add("migrate")).Returns(Task.CompletedTask);
        using var sync = NewSync(migration.Object);

        await sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(["materialize False", "link False", "migrate"], _calls);
    }

    [Fact]
    public async Task AFailureInTheMigrationDoesNotFailTheSync()
    {
        var migration = new Mock<ILayoutMigration>();
        migration.Setup(run => run.RunAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException(TestValues.Host));
        using var sync = NewSync(migration.Object);

        Assert.Equal(SyncStartResult.Started, await sync.RunAsync(CancellationToken.None));

        Assert.False(sync.GetStatus().IsRunning);
        Assert.DoesNotContain(_fixture.Logger.Lines, line => line.Contains(TestValues.Host, StringComparison.Ordinal));
        Assert.Contains(_fixture.Logger.Lines, line => line.Contains(nameof(IOException), StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSyncRecordsTheLibrariesOnTheFolderBeforeLinkingThemAgain()
    {
        var changes = new Mock<ILibraryChangeTracker>();
        changes.Setup(tracker => tracker.Record()).Callback(() => _calls.Add("record"));
        using var sync = NewSync(null, changes.Object);

        await sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(["record", "materialize False", "link False"], _calls);
    }

    [Fact]
    public async Task AFailureInLinkingDoesNotFailTheSync()
    {
        _linker.Setup(linker => linker.LinkAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException(TestValues.Host));

        Assert.Equal(SyncStartResult.Started, await _sync.RunAsync(CancellationToken.None));

        Assert.False(_sync.GetStatus().IsRunning);
        Assert.DoesNotContain(_fixture.Logger.Lines, line => line.Contains(TestValues.Host, StringComparison.Ordinal));
        Assert.Contains(_fixture.Logger.Lines, line => line.Contains(nameof(IOException), StringComparison.Ordinal));
    }

    private CatalogSyncService NewSync(ILayoutMigration? migration, ILibraryChangeTracker? changes = null) => new(
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
        changes: changes);
}
