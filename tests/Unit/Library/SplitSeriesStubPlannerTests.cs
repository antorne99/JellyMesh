using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// Two remote series of one server that were one work and are no longer: no two works share a row of the registry.
public class SplitSeriesStubPlannerTests
{
    private const string Shows = TestLibraries.Shows;

    private static readonly Guid _server = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly DateTimeOffset _now = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly string _longName = new('a', 1000);

    private static RemoteItem Series(string id, string name = "Show", int? year = 2020, string? tvdb = null) => TestItems.Series(id, name) with
    {
        ProductionYear = year,
        ProviderIds = tvdb is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["Tvdb"] = tvdb }
    };

    private static RemoteItem Episode(string seriesId, int number) => TestItems.Episode(seriesId + "e" + number, seriesId, 1, number);

    private static StubState Sync(StubState state, RemoteItem[] series, RemoteItem[] episodes, params ManualMerge[] merges)
        => Sync(false, state, series, episodes, merges);

    private static StubState Sync(bool isFlat, StubState state, RemoteItem[] series, RemoteItem[] episodes, params ManualMerge[] merges)
        => Plan(isFlat, state, series, episodes, merges).State;

    private static StubPlan Plan(bool isFlat, StubState state, RemoteItem[] series, RemoteItem[] episodes, params ManualMerge[] merges)
    {
        var catalog = new ServerCatalog
        {
            ServerId = _server,
            State = ServerSyncState.Complete,
            Items = [
                .. series.Select(item => RemoteItemMapper.ToIndexedItem(item, RemoteItemType.Series, Shows, _now)),
                .. series.Select(item => RemoteItemMapper.ToIndexedItem(TestItems.Season(item.Id + "n1", item.Id, 1), RemoteItemType.Season, Shows, _now)),
                .. episodes.Select(item => RemoteItemMapper.ToIndexedItem(item, RemoteItemType.Episode, Shows, _now))
            ]
        };

        var contents = DedupEngine.Resolve([], CatalogItemMapper.ToCatalogItems(catalog), merges).Decisions
            .Select(decision => new ResolvedContent(decision.Identity, false, Source(decision.Winner!), [])
            {
                RemoteItems = decision.RemoteItems.Select(item => new ResolvedItem(_server, item.ItemId)).ToList()
            })
            .ToList();
        var input = new StubPlanInput(
            new FederationView(IndexHealth.Ok, 0, contents, []),
            new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = IndexHealth.Ok, Servers = [catalog] },
            state,
            [new RemoteServerInfo(_server, "Casa", "http://server", RemoteMode.Standard, 0, [Shows], null, null)],
            true,
            id => "http://127.0.0.1:8096/JellyMesh/Play/" + id,
            _ => null)
        {
            IgnoreRules = new FakeIgnoreRules()
        };
        var plan = isFlat ? StubPlanner.Plan(input) : StubPlanner.PlanByServer(input);

        // No two rows of one id, no two files of one path, and no row left without an episode.
        Assert.Equal(plan.Series.Count, plan.Series.Select(work => work.Entry.Id).Distinct().Count());
        var written = plan.Series.Select(work => work.NfoPath)
            .Concat(plan.Series.SelectMany(work => work.Seasons).Select(season => season.NfoPath))
            .Concat(plan.Stubs.Select(work => work.NfoPath))
            .ToList();
        Assert.Equal(written.Count, written.Distinct().Count());
        var next = plan.State;
        Assert.Equal(next.Series.Count, next.Series.Select(entry => entry.Id).Distinct().Count());
        Assert.Equal(next.Stubs.Count, next.Stubs.Select(stub => stub.RelativePath).Distinct().Count());
        Assert.All(next.Series, entry => Assert.Contains(next.Stubs, stub => stub.SeriesStubId == entry.Id));
        Assert.All(next.Stubs, stub => Assert.Contains(next.Series, entry => entry.Id == stub.SeriesStubId));
        return plan;
    }

    private static ResolvedSource Source(RemoteSource source)
        => new(_server, "Casa", ServerSyncState.Complete, source.ItemId, source.MediaSourceId, null, source.Quality, _now);

    private static void AssertKept(StubState old, StubState next)
    {
        foreach (var stub in old.Stubs)
        {
            var kept = Assert.Single(next.Stubs, entry => entry.Id == stub.Id);
            Assert.Equal((stub.RelativePath, stub.SeriesStubId), (kept.RelativePath, kept.SeriesStubId));
        }
    }

    // The same series twice as one work, then as two, each with its two episodes.
    private static void AssertSplits(RemoteItem[] together, RemoteItem[] apart, params ManualMerge[] merges)
    {
        RemoteItem[] episodes = [Episode("s1", 1), Episode("s1", 2), Episode("s2", 1), Episode("s2", 2)];
        var first = Sync(StubState.Empty, together, episodes, merges);
        Assert.Equal(2, Assert.Single(first.Series).Sources.Count);
        Assert.Equal(2, first.Stubs.Count);

        var second = Sync(first, apart, episodes);
        Assert.Equal(2, second.Series.Count);
        Assert.Equal(4, second.Stubs.Count);
        AssertKept(first, second);
        Assert.All(second.Series, series => Assert.Single(series.Sources));

        var third = Sync(second, apart, [.. episodes, Episode("s1", 3), Episode("s2", 3)]);
        Assert.Equal(second.Series.Select(series => series.Id).Order(), third.Series.Select(series => series.Id).Order());
        Assert.Equal(6, third.Stubs.Count);
        AssertKept(second, third);

        // Together again, the stubs stay where they are, those of one episode merged.
        var fourth = Sync(third, together, episodes, merges);
        Assert.NotEmpty(fourth.Stubs);
    }

    [Fact]
    public void AnotherProviderId()
        => AssertSplits([Series("s1", tvdb: "71"), Series("s2", tvdb: "71")], [Series("s1", tvdb: "71"), Series("s2", tvdb: "72")]);

    [Fact]
    public void OtherProviderIdsOnBoth()
        => AssertSplits([Series("s1", tvdb: "71"), Series("s2", tvdb: "71")], [Series("s1", tvdb: "72"), Series("s2", tvdb: "73")]);

    [Fact]
    public void AYearLost()
        => AssertSplits([Series("s1"), Series("s2")], [Series("s1", year: null), Series("s2", year: null)]);

    [Fact]
    public void AnotherName()
        => AssertSplits([Series("s1"), Series("s2")], [Series("s1"), Series("s2", "Other show")]);

    [Fact]
    public void ALongName()
        => AssertSplits([Series("s1"), Series("s2")], [Series("s1", _longName), Series("s2", _longName)]);

    [Fact]
    public void AnotherProviderIdInTheFlatLayout()
    {
        RemoteItem[] episodes = [Episode("s1", 1), Episode("s2", 1)];
        var first = Sync(true, StubState.Empty, [Series("s1", tvdb: "71"), Series("s2", tvdb: "71")], episodes);

        var second = Sync(true, first, [Series("s1", tvdb: "72"), Series("s2", tvdb: "73")], episodes);

        Assert.Single(first.Series);
        Assert.Equal(2, second.Series.Count);
        AssertKept(first, second);
    }

    [Fact]
    public void AMergeUndone()
    {
        RemoteItem[] series = [Series("s1", tvdb: "71"), Series("s2", tvdb: "72")];
        var merge = new ManualMerge
        {
            Id = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000"),
            CreatedUtc = _now.UtcDateTime,
            Copies = [Copy("s1", "71"), Copy("s2", "72")]
        };

        AssertSplits(series, series, merge);
    }

    [Fact]
    public void ASeriesWhoseEpisodesAllHaveAStubGetsItsRowWithItsNextOne()
    {
        RemoteItem[] apart = [Series("s1", tvdb: "71"), Series("s2", tvdb: "72")];
        RemoteItem[] episodes = [Episode("s1", 1), Episode("s1", 2), Episode("s2", 3), Episode("s2", 4)];
        var first = Sync(StubState.Empty, [Series("s1", tvdb: "71"), Series("s2", tvdb: "71")], episodes);

        var second = Sync(first, apart, episodes);
        Assert.Single(second.Series);
        AssertKept(first, second);

        var third = Sync(second, apart, [.. episodes, Episode("s2", 5)]);
        Assert.Equal(2, third.Series.Count);
        AssertKept(second, third);
        Assert.NotEqual(first.Series[0].Id, Assert.Single(third.Stubs, stub => stub.EpisodeNumber == 5).SeriesStubId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEpisodeOfANumberItsFolderHasFromTheOtherSeriesGetsItsOwnName(bool isFlat)
    {
        RemoteItem[] apart = [Series("s1", tvdb: "71"), Series("s2", tvdb: "72")];
        RemoteItem[] episodes = [Episode("s1", 1), Episode("s1", 2), Episode("s2", 3), Episode("s2", 4)];
        var first = Sync(isFlat, StubState.Empty, [Series("s1", tvdb: "71"), Series("s2", tvdb: "71")], episodes);
        var second = Sync(isFlat, first, apart, episodes);

        var third = Sync(isFlat, second, apart, [.. episodes, Episode("s1", 3)]);

        AssertKept(second, third);
        var added = Added(second, third);
        var taken = Assert.Single(second.Stubs, stub => stub.EpisodeNumber == 3).RelativePath;
        var label = isFlat ? string.Empty : " - Casa";
        Assert.Equal(taken.Replace("S01E03", $"S01E03 [{added.Id[..8]}]", StringComparison.Ordinal), added.RelativePath);
        Assert.EndsWith($"S01E03 [{added.Id[..8]}]{label}.strm", added.RelativePath, StringComparison.Ordinal);

        // The name stays as it is at the next sync.
        AssertKept(third, Sync(isFlat, third, apart, [.. episodes, Episode("s1", 3)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEpisodeOfTheOldNumberOfAnotherGetsItsOwnName(bool isFlat)
    {
        RemoteItem[] series = [Series("s1")];
        var first = Sync(isFlat, StubState.Empty, series, [Episode("s1", 1)]);
        var renumbered = Episode("s1", 1) with { IndexNumber = 2 };
        var second = Sync(isFlat, first, series, [renumbered]);

        var third = Sync(isFlat, second, series, [renumbered, Episode("s1", 1) with { Id = "new" }]);

        AssertKept(first, third);
        Assert.Contains($"S01E01 [{Added(second, third).Id[..8]}]", Added(second, third).RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEpisodePathThatDiffersOnlyInCaseIsTaken()
    {
        var first = Sync(StubState.Empty, [Series("s1")], [Episode("s1", 1)]);
        var old = first with { Stubs = [first.Stubs[0] with { RelativePath = first.Stubs[0].RelativePath.Replace("Show S01E01", "SHOW S01E02", StringComparison.Ordinal) }] };

        var next = Sync(old, [Series("s1")], [Episode("s1", 1), Episode("s1", 2)]);

        Assert.Contains($"S01E02 [{Added(old, next).Id[..8]}]", Added(old, next).RelativePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASeriesWhoseRowIsGoneGetsItAgainInTheFolderOfItsStubs(bool isFlat)
    {
        RemoteItem[] series = [Series("s1")];
        var old = WithoutRows(Sync(isFlat, StubState.Empty, series, [Episode("s1", 1), Episode("s1", 2)]), "Show (2020)", "Show (2020) [12345678]");

        var plan = Plan(isFlat, old, series, [Episode("s1", 1), Episode("s1", 2), Episode("s1", 3)]);

        var row = Assert.Single(plan.Series);
        Assert.False(row.IsNew);
        Assert.True(row.Entry.NeedsRefresh);
        Assert.Null(row.Entry.ContentHash);
        Assert.Single(row.Seasons);
        Assert.EndsWith(Path.Combine("Show (2020) [12345678]", "tvshow.nfo"), row.NfoPath, StringComparison.Ordinal);
        Assert.All(old.Stubs, stub => Assert.Equal(stub.RelativePath, Assert.Single(plan.State.Stubs, entry => entry.Id == stub.Id).RelativePath));
        Assert.All(plan.State.Stubs, stub => Assert.Equal(row.Entry.RelativePath, FolderOf(stub)));

        var next = Plan(isFlat, plan.State, series, [Episode("s1", 1), Episode("s1", 2), Episode("s1", 3)]);
        Assert.Equal(row.Entry.Id, Assert.Single(next.Series).Entry.Id);
        AssertKept(plan.State, next.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANewSeriesOfTheSameNameLeavesTheFolderOfARowThatIsGone(bool isFlat)
    {
        var old = WithoutRows(Sync(isFlat, StubState.Empty, [Series("s1", tvdb: "71")], [Episode("s1", 1)]));

        var next = Sync(isFlat, old, [Series("s0", tvdb: "70"), Series("s1", tvdb: "71")], [Episode("s0", 1), Episode("s1", 1)]);

        Assert.Equal(2, next.Series.Count);
        var kept = Assert.Single(next.Stubs, stub => stub.Id == old.Stubs[0].Id);
        Assert.Equal(FolderOf(kept), Assert.Single(next.Series, entry => entry.Id == kept.SeriesStubId).RelativePath);
        Assert.NotEqual(FolderOf(kept), FolderOf(Added(old, next)));
    }

    [Fact]
    public void AStubInTheFolderOfAnotherRowJoinsItWhenItsOwnIsGone()
    {
        RemoteItem[] series = [Series("s1", tvdb: "71"), Series("s2", tvdb: "72")];
        var first = Sync(StubState.Empty, series, [Episode("s1", 1), Episode("s2", 2)]);
        var kept = Assert.Single(first.Stubs, stub => stub.EpisodeNumber == 1);
        var row = Assert.Single(first.Series, entry => entry.Id == kept.SeriesStubId);
        var old = first with
        {
            Series = [row],
            Stubs = first.Stubs.Select(stub => stub.Id == kept.Id ? stub : stub with { RelativePath = kept.RelativePath.Replace("S01E01", "S01E02", StringComparison.Ordinal) }).ToList()
        };

        var second = Sync(old, series, [Episode("s1", 1), Episode("s2", 2)]);
        Assert.Equal(row.Id, Assert.Single(second.Series).Id);
        Assert.All(second.Stubs, stub => Assert.Equal(row.Id, stub.SeriesStubId));

        var third = Sync(second, series, [Episode("s1", 1), Episode("s2", 2), Episode("s2", 3)]);
        Assert.Equal(2, third.Series.Count);
        AssertKept(second, third);
    }

    [Fact]
    public void TwoSeriesInOneFolderWithoutARowGetOne()
    {
        RemoteItem[] apart = [Series("s1", tvdb: "71"), Series("s2", tvdb: "72")];
        RemoteItem[] episodes = [Episode("s1", 1), Episode("s2", 2)];
        var old = WithoutRows(Sync(StubState.Empty, [Series("s1", tvdb: "71"), Series("s2", tvdb: "71")], episodes));

        var next = Sync(old, apart, episodes);

        Assert.Equal(FolderOf(old.Stubs[0]), Assert.Single(next.Series).RelativePath);
        AssertKept(next, Sync(next, apart, episodes));
    }

    [Fact]
    public void AStubInAFolderWithoutARowStaysInItWhenItsWorkHasAnotherFolder()
    {
        // The first series was split: its second part is in the folder of the other one, whose row is gone.
        RemoteItem[] series = [Series("s1", tvdb: "71"), Series("s2", tvdb: "72")];
        RemoteItem[] episodes = [Episode("s1", 1), Episode("s1", 2), Episode("s2", 3)];
        var first = Sync(StubState.Empty, series, episodes);
        var other = Assert.Single(first.Stubs, stub => stub.EpisodeNumber == 3);
        var folder = FolderOf(other);
        var row = Assert.Single(first.Series, entry => entry.RelativePath != folder);
        var old = first with
        {
            Series = [row],
            Stubs = first.Stubs.Select(stub => stub.EpisodeNumber == 2 ? stub with { SeriesStubId = other.SeriesStubId, RelativePath = other.RelativePath.Replace("S01E03", "S01E02", StringComparison.Ordinal) } : stub).ToList()
        };

        var next = Sync(old, series, episodes);

        Assert.Equal(2, next.Series.Count);
        AssertInTheirFolder(next);
        AssertKept(next, Sync(next, series, episodes));
    }

    [Fact]
    public void TwoFoldersOfOneWorkWithoutARowGetOneEach()
    {
        RemoteItem[] series = [Series("s1")];
        RemoteItem[] episodes = [Episode("s1", 1), Episode("s1", 2)];
        var first = WithoutRows(Sync(StubState.Empty, series, episodes));
        var old = first with
        {
            Stubs = first.Stubs.Select(stub => stub.EpisodeNumber == 2 ? stub with { RelativePath = stub.RelativePath.Replace("Show (2020)", "Show", StringComparison.Ordinal) } : stub).ToList()
        };

        var second = Plan(false, old, series, episodes);
        AssertInTheirFolder(second.State);
        Assert.Equal(2, second.State.Series.Count);

        // The work stays with one of them, and the other gets its tvshow.nfo (§10 row 562).
        var third = Plan(false, second.State, series, episodes);
        Assert.Equal(Claimed(second).Entry.Id, Claimed(third).Entry.Id);
        Assert.Equal(2, third.Series.Count);
        AssertKept(second.State, third.State);
    }

    [Fact]
    public void AStubThatWaitsInAFolderWithoutARowGetsItsRowToo()
    {
        RemoteItem[] series = [Series("s1")];
        var first = WithoutRows(Sync(StubState.Empty, series, [Episode("s1", 1), Episode("s1", 2)]));

        // The second stub waits for its removal: its episode is of the first one now.
        var old = first with { Stubs = first.Stubs.Select(stub => stub.EpisodeNumber == 2 ? stub with { IdentityKey = "old", Sources = first.Stubs[0].Sources } : stub).ToList() };

        var next = Sync(old, series, [Episode("s1", 1)]);

        Assert.Single(next.Series);
        Assert.Equal(2, next.Stubs.Count);
        AssertInTheirFolder(next);
    }

    [Fact]
    public void AStubInNoSeriesFolderGetsNoRowThere()
    {
        RemoteItem[] series = [Series("s1")];
        var first = WithoutRows(Sync(StubState.Empty, series, [Episode("s1", 1)]));
        var old = first with { Stubs = [first.Stubs[0] with { RelativePath = first.Stubs[0].RelativePath.Replace(Path.Combine("Serie", "Show (2020)"), "Serie", StringComparison.Ordinal) }] };

        var next = Sync(old, series, [Episode("s1", 1)]);

        var row = Assert.Single(next.Series);
        Assert.EndsWith(Path.Combine("Serie", "Show (2020)"), row.RelativePath, StringComparison.Ordinal);
        Assert.Equal((row.Id, old.Stubs[0].RelativePath), (next.Stubs[0].SeriesStubId, next.Stubs[0].RelativePath));
    }

    [Fact]
    public void ASeriesWhoseServerMadeItsItemsAgainGetsItsRowInTheFolderOfItsStubs()
    {
        var old = WithoutRows(Sync(StubState.Empty, [Series("s1")], [Episode("s1", 1)]));

        var next = Sync(old, [Series("s9")], [Episode("s9", 1)]);

        Assert.Single(next.Series);
        AssertInTheirFolder(next);
    }

    [Fact]
    public void AnEpisodeWithoutASeriesGetsItsRowInItsFolder()
    {
        RemoteItem[] episodes = [Episode("s1", 1) with { SeriesId = null }];
        var old = WithoutRows(Sync(StubState.Empty, [], episodes));

        var next = Plan(false, old, [], episodes);

        AssertInTheirFolder(next.State);
        Assert.Single(next.Series);
    }

    // On a tie of source the rows keep their order, written or not (§10 row 562).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWorkOfAnotherKeyTakesTheFirstRowOfItsSource(bool writtenFirst)
    {
        RemoteItem[] episodes = [Episode("s1", 1), Episode("s1", 2)];
        var first = Sync(StubState.Empty, [Series("s1")], episodes);
        var old = first with
        {
            Stubs = first.Stubs.Select(stub => stub.EpisodeNumber == 2 ? stub with { SeriesStubId = "gone", RelativePath = stub.RelativePath.Replace("Show (2020)", "Show", StringComparison.Ordinal) } : stub).ToList()
        };
        var second = Sync(old, [Series("s1")], episodes);
        var written = Assert.Single(second.Series, entry => entry.Id == first.Series[0].Id) with { ContentHash = "written" };
        var made = Assert.Single(second.Series, entry => entry.Id != first.Series[0].Id) with { ContentHash = null };
        SeriesStub[] rows = writtenFirst ? [written, made] : [made, written];

        var third = Plan(false, second with { Series = rows }, [Series("s1", tvdb: "71")], episodes);

        Assert.Equal(rows[0].Id, Claimed(third).Entry.Id);
        Assert.Equal(rows[1].Id, Assert.Single(third.Series, work => work.Entry.Id != rows[0].Id).Entry.Id);
    }

    // A second folder of one title keeps its row and gets the tvshow.nfo of the title, once (§10 row 562).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASecondFolderOfATitleGetsItsTvshowNfoOnce(bool isFlat)
    {
        RemoteItem[] series = [Series("s1")];
        RemoteItem[] episodes = [Episode("s1", 1), Episode("s1", 2)];
        var first = WithoutRows(Sync(isFlat, StubState.Empty, series, episodes));
        var old = first with
        {
            Stubs = first.Stubs.Select(stub => stub.EpisodeNumber == 2 ? stub with { RelativePath = stub.RelativePath.Replace("Show (2020)", "Show", StringComparison.Ordinal) } : stub).ToList()
        };

        var second = Plan(isFlat, old, series, episodes);

        var claimed = Claimed(second);
        var other = Assert.Single(second.Series, work => work.Entry.Id != claimed.Entry.Id);
        Assert.False(other.IsNew);
        Assert.Equal(Path.Combine(other.Entry.RelativePath, "tvshow.nfo"), other.NfoPath);
        Assert.Equal((claimed.Nfo, claimed.ContentHash), (other.Nfo, other.ContentHash));
        Assert.Empty(other.Images);
        Assert.Empty(other.Seasons);
        Assert.Equal(2, claimed.Seasons.Count);
        Assert.Contains(second.State.Stubs, stub => stub.SeriesStubId == other.Entry.Id);

        // Once written, the next plan writes nothing again.
        var written = second.State with
        {
            Series = second.State.Series.Select(entry => entry with { ContentHash = Assert.Single(second.Series, work => work.Entry.Id == entry.Id).ContentHash }).ToList()
        };
        var third = Plan(isFlat, written, series, episodes);
        Assert.Equal(2, third.Series.Count);
        Assert.All(third.Series, work => Assert.Equal(work.Entry.ContentHash, work.ContentHash));
    }

    // A stub with no work, in no series folder, names no row once its own is gone (§10 row 562).
    [Fact]
    public void AWaitingStubWhoseRowIsGoneNamesNone()
    {
        RemoteItem[] series = [Series("s1")];
        var first = Sync(StubState.Empty, series, [Episode("s1", 1), Episode("s1", 2)]);
        var sources = Assert.Single(first.Stubs, stub => stub.EpisodeNumber == 1).Sources;
        var waiting = Assert.Single(first.Stubs, stub => stub.EpisodeNumber == 2) with
        {
            IdentityKey = "old",
            Sources = sources,
            SeriesStubId = "gone"
        };
        waiting = waiting with { RelativePath = waiting.RelativePath.Replace(Path.Combine("Serie", "Show (2020)"), "Serie", StringComparison.Ordinal) };
        var old = first with { Stubs = first.Stubs.Select(stub => stub.Id == waiting.Id ? waiting : stub).ToList() };

        var next = Sync(old, series, [Episode("s1", 1)]);

        var kept = Assert.Single(next.Stubs, stub => stub.Id == waiting.Id);
        Assert.Equal(waiting.RelativePath, kept.RelativePath);
        Assert.Null(kept.SeriesStubId);
    }

    private static void AssertInTheirFolder(StubState state)
        => Assert.All(state.Stubs, stub => Assert.Equal(FolderOf(stub), Assert.Single(state.Series, entry => entry.Id == stub.SeriesStubId).RelativePath));

    // The registry after the rows of its series went, with their folder under another name.
    private static StubState WithoutRows(StubState state, string folder = "", string? other = null) => state with
    {
        Series = [],
        Stubs = other is null ? state.Stubs : state.Stubs.Select(stub => stub with { RelativePath = stub.RelativePath.Replace(folder, other, StringComparison.Ordinal) }).ToList()
    };

    // The work of the row a plan takes: the row of a second folder gets only its tvshow.nfo, with no season (§10 row 562).
    private static SeriesWork Claimed(StubPlan plan) => Assert.Single(plan.Series, work => work.Seasons.Count > 0);

    private static string FolderOf(StubEntry stub) => Path.GetDirectoryName(Path.GetDirectoryName(stub.RelativePath))!;

    private static StubEntry Added(StubState old, StubState next)
        => Assert.Single(next.Stubs, stub => old.Stubs.All(entry => entry.Id != stub.Id));

    [Fact]
    public void ARowHeldByAnotherWorkIsNotTakenByItsSource()
    {
        var key = $"Episode|item:{_server:N}:s1e1";
        var first = Sync(StubState.Empty, [Series("s1", year: null)], [Episode("s1", 1), Episode("s1", 2)]);
        var old = first with { Series = [first.Series[0] with { WorkKey = key }] };

        // An episode that lost its series has the work key the row has, and the other one its source.
        var next = Sync(old, [Series("s1", year: null)], [Episode("s1", 1) with { SeriesId = null }, Episode("s1", 2), Episode("s1", 3)]);

        Assert.Equal(2, next.Series.Count);
        AssertKept(first, next);
    }

    private static MergedCopy Copy(string id, string tvdb) => new()
    {
        Kind = ContentKind.Episode,
        ServerId = _server.ToString("N"),
        ItemId = id,
        Tvdb = tvdb
    };
}
