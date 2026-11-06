using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Index;

public sealed class FederationIndexTests : IDisposable
{
    private static readonly DateTimeOffset _now = new(2026, 11, 4, 3, 0, 0, TimeSpan.Zero);
    private static readonly Guid _first = Guid.NewGuid();
    private static readonly Guid _second = Guid.NewGuid();

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jellymesh-tests-" + Guid.NewGuid().ToString("N"));

    private string MainPath => Path.Combine(_folder, IndexFile.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private FederationIndex NewIndex()
        => new(new IndexFile(_folder, new CapturingLogger<IndexFile>()), new CapturingLogger<FederationIndex>());

    private static IndexedItem Item(string id) => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Movie,
        LibraryId = "library",
        LastSeenUtc = _now
    };

    [Fact]
    public async Task ReplacingAServerLeavesTheOthersAlone()
    {
        using var index = NewIndex();
        await index.ReplaceServerAsync(_first, [Item("a")], _now, null, CancellationToken.None);
        await index.ReplaceServerAsync(_second, [Item("b")], _now, null, CancellationToken.None);

        await index.ReplaceServerAsync(_first, [Item("c")], _now, null, CancellationToken.None);

        Assert.Equal("c", Assert.Single(index.Snapshot.Find(_first)!.Items).ItemId);
        Assert.Equal("b", Assert.Single(index.Snapshot.Find(_second)!.Items).ItemId);
    }

    [Fact]
    public async Task KeepingServersDropsTheOthersForGood()
    {
        using (var index = NewIndex())
        {
            await index.ReplaceServerAsync(_first, [Item("a")], _now, null, CancellationToken.None);
            await index.ReplaceServerAsync(_second, [Item("b")], _now, null, CancellationToken.None);

            await index.KeepServersAsync(new HashSet<Guid> { _second }, CancellationToken.None);
        }

        using var restarted = NewIndex();

        Assert.Null(restarted.Snapshot.Find(_first));
        Assert.Equal("b", Assert.Single(restarted.Snapshot.Find(_second)!.Items).ItemId);
    }

    [Fact]
    public async Task KeepingEveryServerWritesNothing()
    {
        using var index = NewIndex();
        await index.ReplaceServerAsync(_first, [Item("a")], _now, null, CancellationToken.None);
        File.Delete(MainPath);

        Assert.True(await index.KeepServersAsync(new HashSet<Guid> { _first }, CancellationToken.None));

        Assert.False(File.Exists(MainPath));
    }

    [Fact]
    public async Task TheIndexComesBackAfterARestart()
    {
        using (var index = NewIndex())
        {
            await index.ReplaceServerAsync(_first, [Item("a")], _now, null, CancellationToken.None);
            await index.MarkServerFailedAsync(_second, RemoteFailure.Timeout, _now, CancellationToken.None);
        }

        using var restarted = NewIndex();

        Assert.True(restarted.Snapshot.IsAuthoritativeFor(_first));
        Assert.False(restarted.Snapshot.IsAuthoritativeFor(_second));
        Assert.Equal(RemoteFailure.Timeout, restarted.Snapshot.Find(_second)!.LastFailure);
    }

    [Fact]
    public async Task AFailureKeepsTheEntries()
    {
        using var index = NewIndex();
        await index.ReplaceServerAsync(_first, [Item("a")], _now, null, CancellationToken.None);

        await index.MarkServerFailedAsync(_first, RemoteFailure.Unreachable, _now.AddHours(1), CancellationToken.None);

        var catalog = index.Snapshot.Find(_first)!;
        Assert.Single(catalog.Items);
        Assert.Equal(ServerSyncState.Stale, catalog.State);
        Assert.Equal(_now, catalog.LastSuccessUtc);
    }

    [Fact]
    public void AServerThatNeverSyncedIsNotAuthoritative()
    {
        using var index = NewIndex();

        Assert.False(index.Snapshot.IsAuthoritativeFor(_first));
    }

    [Fact]
    public async Task ACorruptIndexLoadsEmptyWithoutThrowingAndStaysFlaggedAcrossRestarts()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(MainPath, "{ not json");

        using (var index = NewIndex())
        {
            Assert.Equal(IndexHealth.Corrupt, index.Snapshot.Health);
            Assert.False(index.Snapshot.IsAuthoritativeFor(_first));

            // A failed sync writes a sound file, yet no catalog came back: the warning has to stay.
            await index.MarkServerFailedAsync(_first, RemoteFailure.Unreachable, _now, CancellationToken.None);
            Assert.Equal(IndexHealth.Corrupt, index.Snapshot.Health);
        }

        using var restarted = NewIndex();

        Assert.Equal(IndexHealth.Corrupt, restarted.Snapshot.Health);
        Assert.False(restarted.Snapshot.IsAuthoritativeFor(_first));
    }

    [Fact]
    public async Task ACatalogThatArrivesClearsTheDamageOnlyForTheServerItCameFrom()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(MainPath, "{ not json");
        using var index = NewIndex();

        await index.ReplaceServerAsync(_first, [Item("a")], _now, null, CancellationToken.None);

        Assert.Equal(IndexHealth.Ok, index.Snapshot.Health);
        Assert.True(index.Snapshot.IsAuthoritativeFor(_first));
        Assert.False(index.Snapshot.IsAuthoritativeFor(_second));
    }

    [Fact]
    public async Task AChangeThatCannotBeSavedIsNotMadeVisible()
    {
        using var index = NewIndex();
        await File.WriteAllTextAsync(_folder, "a file where the folder should be");

        var saved = await index.ReplaceServerAsync(_first, [Item("a")], _now, null, CancellationToken.None);

        Assert.False(saved);
        Assert.Null(index.Snapshot.Find(_first));
        File.Delete(_folder);
    }

    [Fact]
    public async Task ConcurrentWritersLoseNothing()
    {
        using var index = NewIndex();
        var servers = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(servers.Select(server => index.ReplaceServerAsync(server, [Item("a")], _now, null, CancellationToken.None)));

        Assert.Equal(servers.Count, index.Snapshot.Servers.Count);
    }

    [Fact]
    public async Task AMergeAddsAndUpdatesAndRemovesNothing()
    {
        using var index = NewIndex();
        await index.ReplaceServerAsync(_first, [Item("a"), Item("b")], _now, null, CancellationToken.None);
        var later = _now.AddMinutes(15);
        var mark = new SyncMark(later, ["library"]);

        Assert.True(await index.MergeServerAsync(_first, [Item("b") with { Name = "new name" }, Item("c")], later, mark, CancellationToken.None));

        var catalog = index.Snapshot.Find(_first)!;
        Assert.Equal(["a", "b", "c"], catalog.Items.Select(item => item.ItemId));
        Assert.Equal("new name", catalog.Items.Single(item => item.ItemId == "b").Name);
        Assert.Equal(ServerSyncState.Complete, catalog.State);
        Assert.Equal(later, catalog.LastAttemptUtc);
        Assert.Equal(_now, catalog.LastSuccessUtc);
        Assert.Equal(mark, catalog.Mark);
        Assert.True(index.Snapshot.IsAuthoritativeFor(_first));
    }

    [Fact]
    public async Task AMergeNeverMakesAStaleServerAuthoritative()
    {
        using var index = NewIndex();
        await index.ReplaceServerAsync(_first, [Item("a")], _now, null, CancellationToken.None);
        await index.MarkServerFailedAsync(_first, RemoteFailure.Unreachable, _now, CancellationToken.None);
        var before = index.Snapshot;

        await index.MergeServerAsync(_first, [Item("b")], _now, new SyncMark(_now, []), CancellationToken.None);

        Assert.Equal(before.Find(_first), index.Snapshot.Find(_first));
        Assert.False(index.Snapshot.IsAuthoritativeFor(_first));
    }

    [Fact]
    public async Task AMergeForAServerNeverReadAddsNothing()
    {
        using var index = NewIndex();

        await index.MergeServerAsync(_first, [Item("a")], _now, new SyncMark(_now, []), CancellationToken.None);

        Assert.Null(index.Snapshot.Find(_first));
    }

    [Fact]
    public async Task TheMarkSurvivesAFailureAndARestart()
    {
        var mark = new SyncMark(_now, ["library"]);
        using (var index = NewIndex())
        {
            await index.ReplaceServerAsync(_first, [Item("a")], _now, mark, CancellationToken.None);
            await index.MarkServerFailedAsync(_first, RemoteFailure.Timeout, _now, CancellationToken.None);
        }

        using var reloaded = NewIndex();
        var loaded = reloaded.Snapshot.Find(_first)!.Mark!;
        Assert.Equal(mark.ReadStartedUtc, loaded.ReadStartedUtc);
        Assert.Equal(mark.LibraryIds, loaded.LibraryIds);
    }
}
