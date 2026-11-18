using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Tests.Storage;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Index;

public sealed class FederationIndexTests : IDisposable
{
    private static readonly DateTimeOffset _now = new(2026, 11, 4, 3, 0, 0, TimeSpan.Zero);
    private static readonly Guid _first = Guid.NewGuid();
    private static readonly Guid _second = Guid.NewGuid();

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jellymesh-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TestDatabase _database;

    public FederationIndexTests() => _database = new TestDatabase(_folder);

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private FederationIndex NewIndex() => new(_database, new CapturingLogger<FederationIndex>());

    private static IndexedItem Item(string id, DateTimeOffset? seen = null) => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Movie,
        LibraryId = "library",
        LastSeenUtc = seen ?? _now
    };

    // An entry with every kind of nested detail, as a full read stores it.
    private static IndexedItem RichItem(string id, DateTimeOffset seen) => Item(id, seen) with
    {
        Name = "Film",
        Overview = "Plot",
        Genres = ["Drama", "Comedy"],
        People = [new IndexedPerson("Anna", "Lead", "Actor")],
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1", ["Imdb"] = "tt1" },
        ImageTags = new Dictionary<string, string> { ["Primary"] = "tag" },
        BackdropImageTags = ["backdrop"],
        CommunityRating = 7.5f,
        PremiereDate = _now.AddYears(-3),
        Chapters = [new IndexedChapter("One", 0), new IndexedChapter("Two", 600_000_000)],
        Collection = new IndexedBoxSet("Saga", "10"),
        Versions =
        [
            new IndexedVersion
            {
                MediaSourceId = "source",
                Container = "mkv",
                Size = 1_000_000,
                Height = 2160,
                Codec = "hevc",
                VideoRange = "HDR",
                Streams = [new IndexedTrack { Type = "Video", Codec = "hevc", Height = 2160, IsDefault = true }, new IndexedTrack { Type = "Audio", Channels = 6, Language = "ita" }]
            }
        ]
    };

    private static string Json(IndexedItem item) => JsonSerializer.Serialize(item);

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
    public async Task AFullReadKeepsWhatChangedAndDropsWhatIsGoneAcrossARestart()
    {
        using (var index = NewIndex())
        {
            await index.ReplaceServerAsync(_first, [Item("a"), Item("b"), Item("c")], _now, null, CancellationToken.None);
            await index.ReplaceServerAsync(_first, [Item("a"), Item("b") with { Name = "new name" }, Item("d")], _now.AddDays(1), null, CancellationToken.None);
        }

        using var restarted = NewIndex();

        var items = restarted.Snapshot.Find(_first)!.Items;
        Assert.Equal(["a", "b", "d"], items.Select(item => item.ItemId).Order(StringComparer.Ordinal));
        Assert.Equal("new name", items.Single(item => item.ItemId == "b").Name);
    }

    [Fact]
    public async Task AnEntryComesBackWholeAfterARestart()
    {
        var item = RichItem("a", _now);
        using (var index = NewIndex())
        {
            await index.ReplaceServerAsync(_first, [item], _now, null, CancellationToken.None);
        }

        using var restarted = NewIndex();

        Assert.Equal(Json(item), Json(Assert.Single(restarted.Snapshot.Find(_first)!.Items)));
    }

    [Fact]
    public async Task DroppingAServerRemovesItForGood()
    {
        using (var index = NewIndex())
        {
            await index.ReplaceServerAsync(_first, [Item("a")], _now, null, CancellationToken.None);
            await index.ReplaceServerAsync(_second, [Item("b")], _now, null, CancellationToken.None);

            await index.DropServersAsync(new HashSet<Guid> { _first }, CancellationToken.None);
        }

        using var restarted = NewIndex();

        Assert.Null(restarted.Snapshot.Find(_first));
        Assert.Equal("b", Assert.Single(restarted.Snapshot.Find(_second)!.Items).ItemId);
    }

    [Fact]
    public async Task DroppingAServerNotThereWritesNothing()
    {
        using var index = NewIndex();
        await index.ReplaceServerAsync(_first, [Item("a")], _now, null, CancellationToken.None);
        var writes = _database.Writes;

        Assert.True(await index.DropServersAsync(new HashSet<Guid> { _second }, CancellationToken.None));

        Assert.Equal(writes, _database.Writes);
    }

    [Fact]
    public async Task TheOrderOfTheEntriesComesBackAfterARestart()
    {
        using var index = NewIndex();
        await index.ReplaceServerAsync(_first, [Item("a"), Item("b"), Item("c")], _now, null, CancellationToken.None);
        await index.ReplaceServerAsync(_first, [Item("c"), Item("a"), Item("d"), Item("b")], _now, null, CancellationToken.None);

        using var restarted = NewIndex();

        Assert.Equal(["c", "a", "d", "b"], restarted.Snapshot.Find(_first)!.Items.Select(item => item.ItemId));
    }

    [Fact]
    public async Task RowsThatDoNotReadGiveAnEmptyFlaggedIndexWhoseNextChangeStartsAfresh()
    {
        using (var index = NewIndex())
        {
            await index.ReplaceServerAsync(_first, [Item("a")], _now, null, CancellationToken.None);
            await index.ReplaceServerAsync(_second, [Item("b")], _now, null, CancellationToken.None);
        }

        await _database.WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE index_items SET data = '{' WHERE item_id = 'b';";
            command.ExecuteNonQuery();
        }, CancellationToken.None);

        using var damaged = NewIndex();
        Assert.Equal(IndexHealth.Corrupt, damaged.Snapshot.Health);
        Assert.Empty(damaged.Snapshot.Servers);

        Assert.True(await damaged.ReplaceServerAsync(_first, [Item("c")], _now, null, CancellationToken.None));

        using var restarted = NewIndex();
        Assert.Equal(IndexHealth.Ok, restarted.Snapshot.Health);
        Assert.Equal("c", Assert.Single(Assert.Single(restarted.Snapshot.Servers).Items).ItemId);
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
        Assert.Equal(IndexHealth.Ok, restarted.Snapshot.Health);
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

    // As a damaged index file did: empty for lack of data, flagged, and never taken for a complete read.
    [Fact]
    public async Task ASuspendedStoreGivesAnEmptyFlaggedIndexThatTakesNoChange()
    {
        using (var index = NewIndex())
        {
            await index.ReplaceServerAsync(_first, [Item("a")], _now, null, CancellationToken.None);
        }

        _database.Suspended = true;
        using var suspended = NewIndex();

        Assert.Equal(IndexHealth.Corrupt, suspended.Snapshot.Health);
        Assert.False(suspended.Snapshot.IsAuthoritativeFor(_first));
        Assert.False(await suspended.ReplaceServerAsync(_second, [Item("b")], _now, null, CancellationToken.None));
        Assert.Null(suspended.Snapshot.Find(_second));
    }

    [Fact]
    public async Task AfterTheStoreComesBackTheFirstChangeStartsFromWhatItHolds()
    {
        using (var index = NewIndex())
        {
            await index.ReplaceServerAsync(_first, [Item("a")], _now, null, CancellationToken.None);
        }

        _database.Suspended = true;
        using var recovering = NewIndex();
        _database.Suspended = false;

        Assert.True(await recovering.ReplaceServerAsync(_second, [Item("b")], _now, null, CancellationToken.None));

        Assert.Equal(IndexHealth.Ok, recovering.Snapshot.Health);
        Assert.Equal("a", Assert.Single(recovering.Snapshot.Find(_first)!.Items).ItemId);
        using var restarted = NewIndex();
        Assert.Equal("a", Assert.Single(restarted.Snapshot.Find(_first)!.Items).ItemId);
        Assert.Equal("b", Assert.Single(restarted.Snapshot.Find(_second)!.Items).ItemId);
    }

    [Fact]
    public async Task AChangeThatCannotBeSavedIsNotMadeVisible()
    {
        using var index = NewIndex();
        _database.InterruptNextWrite();

        var saved = await index.ReplaceServerAsync(_first, [Item("a")], _now, null, CancellationToken.None);

        Assert.False(saved);
        Assert.Null(index.Snapshot.Find(_first));
    }

    // Transazioni: a sync stopped halfway through saving a server leaves the state of the sync before.
    [Fact]
    public async Task ASaveInterruptedHalfwayLeavesThePreviousSyncOfThatServer()
    {
        var mark = new SyncMark(_now, ["library"]);
        using (var index = NewIndex())
        {
            await index.ReplaceServerAsync(_first, [Item("a"), Item("b")], _now, mark, CancellationToken.None);
            _database.InterruptNextWrite();

            Assert.False(await index.ReplaceServerAsync(_first, [Item("b") with { Name = "new" }, Item("c")], _now.AddDays(1), new SyncMark(_now.AddDays(1), ["other"]), CancellationToken.None));

            Assert.Equal(["a", "b"], index.Snapshot.Find(_first)!.Items.Select(item => item.ItemId));
        }

        using var restarted = NewIndex();
        var catalog = restarted.Snapshot.Find(_first)!;
        Assert.Equal(["a", "b"], catalog.Items.Select(item => item.ItemId).Order(StringComparer.Ordinal));
        Assert.Null(catalog.Items.Single(item => item.ItemId == "b").Name);
        Assert.Equal(["library"], catalog.Mark!.LibraryIds);
        Assert.Equal(_now, catalog.LastSuccessUtc);
    }

    [Fact]
    public async Task ACancelledSaveLeavesThePreviousSyncOfThatServer()
    {
        using (var index = NewIndex())
        {
            await index.ReplaceServerAsync(_first, [Item("a")], _now, null, CancellationToken.None);
            _database.InterruptNextWrite(new OperationCanceledException());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.ReplaceServerAsync(_first, [Item("c")], _now, null, CancellationToken.None));
        }

        using var restarted = NewIndex();
        Assert.Equal("a", Assert.Single(restarted.Snapshot.Find(_first)!.Items).ItemId);
    }

    // Transazioni: a sync with no changes writes nothing (D27).
    [Fact]
    public async Task AFullReadThatFindsNothingNewWritesNothing()
    {
        using var index = NewIndex();
        await index.ReplaceServerAsync(_first, [RichItem("a", _now), Item("b")], _now, new SyncMark(_now, ["library"]), CancellationToken.None);
        var writes = _database.Writes;
        var later = _now.AddDays(1);

        Assert.True(await index.ReplaceServerAsync(_first, [RichItem("a", later), Item("b", later)], later, new SyncMark(later, ["library"]), CancellationToken.None));

        Assert.Equal(writes, _database.Writes);
        var catalog = index.Snapshot.Find(_first)!;
        Assert.Equal((later, later), (catalog.LastSuccessUtc, catalog.Mark!.ReadStartedUtc));
    }

    [Fact]
    public async Task AFailureRepeatedWritesNothing()
    {
        using var index = NewIndex();
        await index.ReplaceServerAsync(_first, [Item("a")], _now, null, CancellationToken.None);
        await index.MarkServerFailedAsync(_first, RemoteFailure.Unreachable, _now.AddMinutes(15), CancellationToken.None);
        var writes = _database.Writes;

        Assert.True(await index.MarkServerFailedAsync(_first, RemoteFailure.Unreachable, _now.AddMinutes(30), CancellationToken.None));

        Assert.Equal(writes, _database.Writes);
        Assert.Equal(_now.AddMinutes(30), index.Snapshot.Find(_first)!.LastAttemptUtc);
    }

    [Fact]
    public async Task ConcurrentWritersLoseNothing()
    {
        using var index = NewIndex();
        var servers = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(servers.Select(server => index.ReplaceServerAsync(server, [Item("a")], _now, null, CancellationToken.None)));

        Assert.Equal(servers.Count, index.Snapshot.Servers.Count);
        using var restarted = NewIndex();
        Assert.Equal(servers.Count, restarted.Snapshot.Servers.Count);
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
        using var restarted = NewIndex();
        Assert.Equal("new name", restarted.Snapshot.Find(_first)!.Items.Single(item => item.ItemId == "b").Name);
        Assert.Equal(later, restarted.Snapshot.Find(_first)!.Mark!.ReadStartedUtc);
    }

    [Fact]
    public async Task AMergeWithNoChangesWritesNothing()
    {
        using var index = NewIndex();
        await index.ReplaceServerAsync(_first, [Item("a")], _now, new SyncMark(_now, ["library"]), CancellationToken.None);
        var writes = _database.Writes;
        var later = _now.AddMinutes(15);
        var mark = new SyncMark(later, ["library"]);

        Assert.True(await index.MergeServerAsync(_first, [], later, mark, CancellationToken.None));
        Assert.True(await index.MergeServerAsync(_first, [Item("a", later)], later, mark, CancellationToken.None));

        Assert.Equal(writes, _database.Writes);
        var catalog = index.Snapshot.Find(_first)!;
        Assert.Equal((later, mark), (catalog.LastAttemptUtc, catalog.Mark));
    }

    // The sync tells an unchanged catalog by its list of items (Q386).
    [Fact]
    public async Task AMergeWithNoChangesKeepsTheListOfItems()
    {
        using var index = NewIndex();
        await index.ReplaceServerAsync(_first, [Item("a")], _now, null, CancellationToken.None);
        var items = index.Snapshot.Find(_first)!.Items;

        await index.MergeServerAsync(_first, [], _now.AddMinutes(15), new SyncMark(_now.AddMinutes(15), ["library"]), CancellationToken.None);

        Assert.Same(items, index.Snapshot.Find(_first)!.Items);
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
        var mark = new SyncMark(_now, ["library", "other"]);
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
