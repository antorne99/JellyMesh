using System;
using System.Collections.Generic;
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

    private static RemoteItem Series(string id, string name = "Show", int? year = 2020, string? tvdb = null) => TestItems.Series(id, name) with
    {
        ProductionYear = year,
        ProviderIds = tvdb is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["Tvdb"] = tvdb }
    };

    private static RemoteItem Episode(string seriesId, int number) => TestItems.Episode(seriesId + "e" + number, seriesId, 1, number);

    private static StubState Sync(StubState state, RemoteItem[] series, RemoteItem[] episodes, params ManualMerge[] merges)
        => Sync(false, state, series, episodes, merges);

    private static StubState Sync(bool isFlat, StubState state, RemoteItem[] series, RemoteItem[] episodes, params ManualMerge[] merges)
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
            _ => null);
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
        return next;
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
