using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Storage;
using Jellyfin.Plugin.JellyMesh.Tests.Storage;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Index;

public sealed class ManualMergeStoreTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private ManualMergeStore Store() => new(_database, new CapturingLogger<ManualMergeStore>());

    private static ManualMerge Merge() => new()
    {
        Id = Guid.NewGuid(),
        CreatedUtc = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero),
        Copies =
        [
            new MergedCopy { ServerId = "a", ItemId = "1", Tmdb = "7", Title = "Film", Year = 2020 },
            new MergedCopy { ServerId = "local", ItemId = "2", IsLocal = true }
        ]
    };

    [Fact]
    public void AnEmptyDatabaseMeansNoMerges()
    {
        using var store = Store();

        Assert.Empty(store.Merges);
        Assert.False(store.IsDamaged);
    }

    [Fact]
    public async Task MergesSurviveARestart()
    {
        var merge = Merge();
        using (var store = Store())
        {
            await store.UpdateAsync(_ => [merge], CancellationToken.None);
        }

        using var reloaded = Store();

        var read = Assert.Single(reloaded.Merges);
        Assert.Equal(merge.Id, read.Id);
        Assert.Equal(merge.CreatedUtc, read.CreatedUtc);
        Assert.Equal(merge.Copies, read.Copies);
    }

    [Fact]
    public async Task ASeriesCopyKeepsItsKindAndTvdb()
    {
        var series = new MergedCopy { ServerId = "a", ItemId = "s", Kind = ContentKind.Episode, Tvdb = "71", Title = "Show" };
        using (var store = Store())
        {
            await store.UpdateAsync(_ => [Merge() with { Copies = [series, Merge().Copies[0]] }], CancellationToken.None);
        }

        using var reloaded = Store();

        var copies = Assert.Single(reloaded.Merges).Copies;
        Assert.Equal(series, copies[0]);
        Assert.Equal(ContentKind.Movie, copies[1].Kind);
    }

    [Fact]
    public async Task EachMergeIsARowWithACopyPerRow_AndAnUndoTakesThemAway()
    {
        var kept = Merge();
        var undone = Merge();
        using var store = Store();
        await store.UpdateAsync(_ => [kept, undone], CancellationToken.None);
        Assert.Equal(2, _database.Count("manual_merges"));
        Assert.Equal(4, _database.Count("manual_merge_copies"));

        await store.UpdateAsync(current => [.. current], CancellationToken.None);
        await store.UpdateAsync(current => [current[0] with { Copies = [.. current[0].Copies, new MergedCopy { ServerId = "b", ItemId = "3" }] }], CancellationToken.None);

        Assert.Equal(1, _database.Count("manual_merges"));
        Assert.Equal(3, _database.Count("manual_merge_copies"));
        using var reloaded = Store();
        Assert.Equal(3, Assert.Single(reloaded.Merges).Copies.Count);
    }

    [Fact]
    public async Task AChangeStartsFromTheTable_NotFromAnOlderCopy()
    {
        using var first = Store();
        using var second = Store();
        Assert.Empty(second.Merges);
        await first.UpdateAsync(_ => [Merge()], CancellationToken.None);

        await second.UpdateAsync(current => [.. current, Merge()], CancellationToken.None);

        Assert.Equal(2, second.Merges.Count);
        Assert.Equal(2, _database.Count("manual_merges"));
    }

    [Fact]
    public async Task AChangeThatFails_LeavesTheMergesAsTheyWere()
    {
        var merge = Merge();
        using var store = Store();
        await store.UpdateAsync(_ => [merge], CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(_ => throw new InvalidOperationException(), CancellationToken.None));

        Assert.Same(merge, Assert.Single(store.Merges));
        Assert.Equal(1, _database.Count("manual_merges"));
    }

    [Fact]
    public async Task WhileTheDatabaseIsSuspended_TheMergesAreNotApplied_NotSaved_AndReadAgainLater()
    {
        using (var earlier = Store())
        {
            await earlier.UpdateAsync(_ => [Merge()], CancellationToken.None);
        }

        _database.Suspended = true;
        var logger = new CapturingLogger<ManualMergeStore>();
        using var store = new ManualMergeStore(_database, logger);

        Assert.Empty(store.Merges);
        Assert.True(store.IsDamaged);
        await Assert.ThrowsAsync<StoreSuspendedException>(() => store.UpdateAsync(_ => [], CancellationToken.None));
        Assert.Single(logger.Lines);

        _database.Suspended = false;
        Assert.Single(store.Merges);
        Assert.False(store.IsDamaged);
    }

    // A database opened again, as from a copy, may hold other merges: they are read again before use.
    [Fact]
    public async Task AfterTheDatabaseIsOpenedAgainTheMergesAreReadAgain()
    {
        using var first = Store();
        using var second = Store();
        Assert.Empty(second.Merges);
        await first.UpdateAsync(_ => [Merge()], CancellationToken.None);

        _database.Reopen();

        Assert.Single(second.Merges);
    }
}
