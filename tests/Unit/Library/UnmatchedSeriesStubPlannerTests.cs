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

// A remote series that matches by nothing, without IDs and without a year, is one series of its server, with one folder and
// one row in the registry.
public class UnmatchedSeriesStubPlannerTests
{
    private const string Shows = TestLibraries.Shows;

    private static readonly Guid _server = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly DateTimeOffset _now = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    private static StubState Sync(StubState state, string name, int? year, params RemoteItem[] episodes)
    {
        var series = TestItems.Series("s", name) with { ProductionYear = year, ProviderIds = new Dictionary<string, string?>() };
        var catalog = new ServerCatalog
        {
            ServerId = _server,
            State = ServerSyncState.Complete,
            Items = [
                RemoteItemMapper.ToIndexedItem(series, RemoteItemType.Series, Shows, _now),
                .. episodes.Select(episode => RemoteItemMapper.ToIndexedItem(episode with { SeriesName = name }, RemoteItemType.Episode, Shows, _now))
            ]
        };

        var contents = DedupEngine.Resolve([], CatalogItemMapper.ToCatalogItems(catalog)).Decisions
            .Select(decision => new ResolvedContent(decision.Identity, false, null, [])
            {
                RemoteItems = decision.RemoteItems.Select(item => new ResolvedItem(_server, item.ItemId)).ToList()
            })
            .ToList();
        return StubPlanner.PlanByServer(new StubPlanInput(
            new FederationView(IndexHealth.Ok, 0, contents, []),
            new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = IndexHealth.Ok, Servers = [catalog] },
            state,
            [new RemoteServerInfo(_server, "Casa", "http://server", RemoteMode.Standard, 0, [Shows], null, null)],
            true,
            id => "http://127.0.0.1:8096/JellyMesh/Play/" + id,
            _ => null)).State;
    }

    private static RemoteItem Episode(int number, string suffix = "") => TestItems.Episode("e" + number + suffix, "s", 1, number);

    private static void AssertOneSeries(int episodes, StubState state)
    {
        var series = Assert.Single(state.Series);
        Assert.Equal(episodes, state.Stubs.Count);
        Assert.All(state.Stubs, stub => Assert.Equal(series.Id, stub.SeriesStubId));
        Assert.Equal(episodes, state.Stubs.Select(stub => stub.RelativePath).Distinct().Count());
    }

    // The name and year of a series without IDs, that give it no work.
    public static TheoryData<string, int?> Unmatched() => new()
    {
        { "Show", null }
    };

    [Theory]
    [MemberData(nameof(Unmatched))]
    public void AnUnmatchedSeriesIsOneSeries(string name, int? year)
        => AssertOneSeries(2, Sync(StubState.Empty, name, year, Episode(1), Episode(2)));

    [Theory]
    [MemberData(nameof(Unmatched))]
    public void ANewEpisodeJoinsAnUnmatchedSeries(string name, int? year)
    {
        var first = Sync(StubState.Empty, name, year, Episode(1), Episode(2));

        AssertOneSeries(3, Sync(first, name, year, Episode(1), Episode(2), Episode(3)));
    }

    [Theory]
    [MemberData(nameof(Unmatched))]
    public void TwoFilesOfAnEpisodeOfAnUnmatchedSeriesAreOneStub(string name, int? year)
        => AssertOneSeries(1, Sync(StubState.Empty, name, year, Episode(1), Episode(1, "b")));

    [Theory]
    [MemberData(nameof(Unmatched))]
    public void ASeriesKeepsItsFolderWhenItStopsMatchingAndMatchesAgain(string name, int? year)
    {
        var first = Sync(StubState.Empty, "Show", 2020, Episode(1), Episode(2));
        var second = Sync(first, name, year, Episode(1), Episode(2), Episode(3));
        var third = Sync(second, "Show", 2020, Episode(1), Episode(2), Episode(3));

        AssertOneSeries(3, second);
        AssertOneSeries(3, third);
        Assert.Equal(first.Series[0].Id, second.Series[0].Id);
        Assert.Equal(second.Stubs.Select(stub => stub.RelativePath).Order(), third.Stubs.Select(stub => stub.RelativePath).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASeriesSplitByAnOlderVersionKeepsItsStubsAndGetsNoSecondRowOfOneId(bool hasSecondRowOfOneId)
    {
        var old = Split(hasSecondRowOfOneId);

        var next = Sync(old, "Show", null, Episode(1), Episode(2), Episode(3));

        Assert.Equal(["s1", "s2"], next.Series.Select(series => series.Id).Order());
        foreach (var stub in old.Stubs)
        {
            var kept = Assert.Single(next.Stubs, entry => entry.Id == stub.Id);
            Assert.Equal((stub.RelativePath, stub.SeriesStubId), (kept.RelativePath, kept.SeriesStubId));
        }

        var added = Assert.Single(next.Stubs, stub => old.Stubs.All(entry => entry.Id != stub.Id));
        Assert.Contains(added.SeriesStubId, new[] { "s1", "s2" });
        Assert.Equal(3, next.Stubs.Select(stub => stub.RelativePath).Distinct().Count());
    }

    // The registry as it was left by a version that made one series of each episode.
    private static StubState Split(bool hasSecondRowOfOneId)
    {
        var first = Sync(StubState.Empty, "Show", 2020, Episode(1));
        var (series, stub) = (Assert.Single(first.Series), Assert.Single(first.Stubs));
        var otherFolder = series.RelativePath + " other";
        var key = $"Episode|item:{_server:N}:e";
        var rows = new List<SeriesStub>
        {
            series with { Id = "s1", WorkKey = key + 1 },
            series with { Id = "s2", WorkKey = key + 2, RelativePath = otherFolder }
        };
        if (hasSecondRowOfOneId)
        {
            rows.Add(rows[0] with { WorkKey = key + 3 });
        }

        return first with
        {
            Series = rows,
            Stubs = [
                stub with { SeriesStubId = "s1", IdentityKey = key + 1 },
                stub with
                {
                    Id = "stub2",
                    SeriesStubId = "s2",
                    IdentityKey = key + 2,
                    EpisodeNumber = 2,
                    RelativePath = stub.RelativePath.Replace(series.RelativePath, otherFolder, StringComparison.Ordinal),
                    Sources = [stub.Sources[0] with { ItemId = "e2", MediaSourceId = "e2" }]
                }
            ]
        };
    }
}
