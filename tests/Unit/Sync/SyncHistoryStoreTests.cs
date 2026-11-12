using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

public sealed class SyncHistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 3, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jellymesh-history-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_folder, SyncHistoryStore.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private SyncHistoryStore Store(CapturingLogger<SyncHistoryStore>? logger = null) => new(_folder, logger ?? new CapturingLogger<SyncHistoryStore>());

    private static SyncRun Run(int minute, SyncRunOutcome outcome = SyncRunOutcome.Succeeded) => new(
        Start.AddMinutes(minute),
        1500,
        SyncRunKind.Incremental,
        outcome,
        [new SyncRunServer(Guid.Parse("8e3f1c2a-4b5d-4e6f-8a9b-0c1d2e3f4a5b"), "Friend", 12, 3, 2, 1, 0, null)])
    {
        Created = 3,
        Updated = 2,
        Removed = 1
    };

    [Fact]
    public void AMissingFileMeansNoRuns()
    {
        using var store = Store();

        Assert.Empty(store.Runs);
        Assert.False(store.IsDamaged);
    }

    [Fact]
    public async Task RunsSurviveARestart_NewestFirst()
    {
        var first = Run(0);
        var second = Run(15, SyncRunOutcome.PartlyFailed) with
        {
            Servers = [new SyncRunServer(Guid.NewGuid(), "Other", 0, 0, 0, 0, 2, RemoteFailure.Unreachable)]
        };
        using (var store = Store())
        {
            await store.AddAsync(first, CancellationToken.None);
            await store.AddAsync(second, CancellationToken.None);
        }

        using var reloaded = Store();

        Assert.Equal(2, reloaded.Runs.Count);
        Assert.Equal(second.StartedUtc, reloaded.Runs[0].StartedUtc);
        Assert.Equal(second.Outcome, reloaded.Runs[0].Outcome);
        Assert.Equal(second.Servers, reloaded.Runs[0].Servers);
        Assert.Equal(first.Servers, reloaded.Runs[1].Servers);
        Assert.Equal(first.DurationMilliseconds, reloaded.Runs[1].DurationMilliseconds);
        Assert.Equal(3, reloaded.Runs[1].Created);
    }

    [Fact]
    public async Task OnlyTheLastFiftyRunsAreKept()
    {
        using (var store = Store())
        {
            for (var minute = 0; minute < SyncHistoryStore.Capacity + 1; minute++)
            {
                await store.AddAsync(Run(minute), CancellationToken.None);
            }

            Assert.Equal(SyncHistoryStore.Capacity, store.Runs.Count);
            Assert.Equal(Start.AddMinutes(SyncHistoryStore.Capacity), store.Runs[0].StartedUtc);
            Assert.Equal(Start.AddMinutes(1), store.Runs[^1].StartedUtc);
        }

        using var reloaded = Store();
        Assert.Equal(SyncHistoryStore.Capacity, reloaded.Runs.Count);
    }

    [Fact]
    public async Task TheLastSevenReconciliationsAreKept_PastTheLastFifty()
    {
        using (var store = Store())
        {
            for (var minute = 0; minute < 8; minute++)
            {
                await store.AddAsync(Reconciliation(minute), CancellationToken.None);
            }

            for (var minute = 8; minute < 58; minute++)
            {
                await store.AddAsync(Run(minute), CancellationToken.None);
            }

            AssertKept(store, [.. Enumerable.Range(8, 50).Reverse(), .. Enumerable.Range(1, 7).Reverse()]);
        }

        using var reloaded = Store();
        AssertKept(reloaded, [.. Enumerable.Range(8, 50).Reverse(), .. Enumerable.Range(1, 7).Reverse()]);
    }

    [Fact]
    public async Task AReconciliationAmongTheLastFifty_CountsTowardTheSeven_AndShowsOnce()
    {
        using var store = Store();
        for (var minute = 0; minute < 8; minute++)
        {
            await store.AddAsync(Reconciliation(minute), CancellationToken.None);
        }

        for (var minute = 8; minute < 58; minute++)
        {
            await store.AddAsync(minute == 53 ? Reconciliation(minute) : Run(minute), CancellationToken.None);
        }

        AssertKept(store, [.. Enumerable.Range(8, 50).Reverse(), .. Enumerable.Range(2, 6).Reverse()]);
    }

    [Fact]
    public void AFileWrittenBeforeTheTotals_StillLoads()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(
            FilePath,
            "{\"Version\":1,\"Runs\":[{\"StartedUtc\":\"2026-11-12T03:00:00+00:00\",\"DurationMilliseconds\":5,\"Kind\":\"Reconciliation\",\"Outcome\":\"Succeeded\",\"Servers\":[]}]}");

        using var store = Store();

        var run = Assert.Single(store.Runs);
        Assert.Equal(SyncRunKind.Reconciliation, run.Kind);
        Assert.Equal(0, run.Created);
        Assert.False(store.IsDamaged);
    }

    private static SyncRun Reconciliation(int minute) => Run(minute) with { Kind = SyncRunKind.Reconciliation };

    private static void AssertKept(SyncHistoryStore store, int[] minutes) =>
        Assert.Equal(minutes.Select(minute => Start.AddMinutes(minute)), store.Runs.Select(run => run.StartedUtc));

    [Fact]
    public async Task NoTemporaryFileStays()
    {
        using var store = Store();

        await store.AddAsync(Run(0), CancellationToken.None);

        Assert.Equal([SyncHistoryStore.FileName], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Version\":99,\"Runs\":[]}")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":1,\"Runs\":[{\"DurationMilliseconds\":5}]}")]
    [InlineData("{\"Version\":1,\"Runs\":[null]}")]
    [InlineData("{\"Version\":1,\"Runs\":[{\"StartedUtc\":\"2026-11-12T03:00:00+00:00\",\"DurationMilliseconds\":5,\"Kind\":\"Incremental\",\"Outcome\":\"Succeeded\",\"Servers\":[null]}]}")]
    public void ABadFileIsSetAside_AndTheHistoryStartsEmpty(string content)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, content);

        using var store = Store();

        Assert.Empty(store.Runs);
        Assert.True(store.IsDamaged);
        Assert.Equal(content, File.ReadAllText(FilePath + ".damaged"));
    }

    [Fact]
    public async Task ASaveClearsTheDamage()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(FilePath, "not json");
        using var store = Store();

        await store.AddAsync(Run(0), CancellationToken.None);

        Assert.False(store.IsDamaged);
        Assert.True(File.Exists(FilePath + ".damaged"));
    }

    [Fact]
    public async Task ARunThatCannotBeSavedStillShows_AndTheSaveThrows()
    {
        // A file where the folder should be makes every save fail, also as root.
        await File.WriteAllTextAsync(_folder, string.Empty);
        try
        {
            using var store = Store();

            await Assert.ThrowsAnyAsync<IOException>(() => store.AddAsync(Run(0), CancellationToken.None));

            Assert.Single(store.Runs);
        }
        finally
        {
            File.Delete(_folder);
        }
    }

    [Fact]
    public async Task TheFileHoldsNoAddress()
    {
        using var store = Store();

        await store.AddAsync(Run(0), CancellationToken.None);

        var text = await File.ReadAllTextAsync(FilePath);
        Assert.DoesNotContain("http", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestValues.Host, text, StringComparison.OrdinalIgnoreCase);
    }
}
