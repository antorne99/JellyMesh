using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// §10 row 420: the automatic migration syncs before it scans, so the scan finds the new stubs.
public sealed partial class LayoutMigrationTests
{
    private readonly Mock<IMigrationSync> _sync = new();

    [Fact]
    public async Task TheAutomaticMigrationSyncsThenScansThenCarries()
    {
        var (_, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var a = NewFilm(_serverA, "a1");
        var newStub = _stubs.Single(stub => stub.ServerId is not null);
        _stubs.Remove(newStub);
        _items.Remove(a.Id);
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));
        Row(_mario, oldItem).IsFavorite = true;
        var order = new List<string>();

        // The sync writes the new stub and the scan brings its item, as on a server that never synced since its upgrade.
        _sync.Setup(sync => sync.SyncAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            order.Add("sync");
            await _registry.SaveAsync(_registry.State with { Stubs = [.. _registry.State.Stubs, newStub] }, CancellationToken.None);
        });
        _scans.Setup(scans => scans.QueueAndWaitAsync(It.IsAny<CancellationToken>())).Callback(() =>
        {
            order.Add("scan");
            _items[a.Id] = a;
        }).Returns(Task.CompletedTask);
        var migration = await MigrationAsync();

        await migration.Choose(LayoutMigrationMode.Automatic).Job;

        Assert.Equal(["sync", "scan", "scan"], order);
        Assert.True(Row(_mario, a).IsFavorite);
        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
        Assert.Equal(AutomaticMigrationError.None, _store.Configuration.LayoutMigration.Error);
    }

    [Fact]
    public async Task TheScanWaitsForTheSyncToEnd()
    {
        OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));
        var sync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sync.Setup(s => s.SyncAsync(It.IsAny<CancellationToken>())).Returns(sync.Task);
        var migration = await MigrationAsync();

        var job = migration.Choose(LayoutMigrationMode.Automatic).Job;
        await WaitForAsync(() => _sync.Invocations.Count == 1);
        await Task.Delay(50);

        Assert.Equal(0, _scansQueued);
        Assert.True(migration.GetStatus(Root)!.Running);
        Assert.Equal(AutomaticMigrationStep.Scan, _store.Configuration.LayoutMigration.Step);

        sync.SetResult();
        await job;

        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
    }

    [Fact]
    public async Task ASyncThatFailsLeavesTheTitlesWaitingWithNoError()
    {
        // A sync that cannot finish is like a server that failed: the scan and the carry go on, and the hooks retry.
        var (_, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var a = NewFilm(_serverA, "a1");
        _items.Remove(a.Id);
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));
        Row(_mario, oldItem).IsFavorite = true;
        _sync.Setup(sync => sync.SyncAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("/secret/path"));
        var migration = await MigrationAsync();

        await migration.Choose(LayoutMigrationMode.Automatic).Job;

        Assert.Equal(1, _scansQueued);
        Assert.Equal(AutomaticMigrationStep.Carry, _store.Configuration.LayoutMigration.Step);
        Assert.Equal(AutomaticMigrationError.None, _store.Configuration.LayoutMigration.Error);
        Assert.DoesNotContain(_logger.Lines, line => line.Contains("/secret/path", StringComparison.Ordinal));

        _items[a.Id] = a;
        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.True(Row(_mario, a).IsFavorite);
        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
    }

    [Fact]
    public async Task TheHooksAtTheCarryDoNotSync()
    {
        // The sync runs its hook at its end, so the carry from a hook must not start another.
        OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms, ServerFilms(_serverA), ServerFilms(_serverB)));
        _store.Configuration.LayoutMigration.Mode = LayoutMigrationMode.Automatic;
        _store.Configuration.LayoutMigration.Step = AutomaticMigrationStep.Carry;
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        _sync.Verify(sync => sync.SyncAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
    }
}
