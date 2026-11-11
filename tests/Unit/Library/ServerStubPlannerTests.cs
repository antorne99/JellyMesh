using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class ServerStubPlannerTests
{
    private const string Library = "films";
    private const string Shows = "shows";

    private static readonly Guid _serverA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid _serverB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");
    private static readonly DateTimeOffset _now = new(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);

    private static string A => _serverA.ToString("D");

    private static string B => _serverB.ToString("D");

    private static IndexedVersion Version(string id, int height = 1080) => new() { MediaSourceId = id, Height = height, Codec = "h264", BitRate = 1000, VideoRange = "SDR" };

    private static IndexedItem Film(string id, string name = "Film", params IndexedVersion[] versions) => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Movie,
        LibraryId = Library,
        LastSeenUtc = _now,
        Name = name,
        ProductionYear = 2020,
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1" },
        Etag = "e1",
        Versions = versions.Length > 0 ? versions : [Version(id)]
    };

    private static IndexedItem SeriesItem(string id, string name = "Show") => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Series,
        LibraryId = Shows,
        LastSeenUtc = _now,
        Name = name,
        ProductionYear = 2020,
        ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "8" }
    };

    private static IndexedItem EpisodeItem(string id, string seriesId, int number, int? end = null) => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Episode,
        LibraryId = Shows,
        LastSeenUtc = _now,
        Name = "Episode",
        SeriesId = seriesId,
        SeriesName = "Show",
        ParentIndexNumber = 1,
        IndexNumber = number,
        IndexNumberEnd = end,
        Versions = [Version(id)]
    };

    private static ResolvedContent Title(string key, params ResolvedItem[] items) => new(
        new ContentIdentity(key, key, ContentKind.Movie, MatchKind.ProviderId),
        false,
        null,
        [])
    {
        RemoteItems = items
    };

    private static ResolvedContent EpisodeTitle(int number, params ResolvedItem[] items) => new(
        new ContentIdentity("show|S1E" + number, "show", ContentKind.Episode, MatchKind.ProviderId, 1, number),
        false,
        null,
        [])
    {
        RemoteItems = items
    };

    private static ResolvedItem OnA(string itemId) => new(_serverA, itemId);

    private static ResolvedItem OnB(string itemId) => new(_serverB, itemId);

    private static ServerCatalog Catalog(Guid server, params IndexedItem[] items) => new()
    {
        ServerId = server,
        State = ServerSyncState.Complete,
        Items = items
    };

    private static RemoteServerInfo Server(Guid id, string name)
        => new(id, name, "http://server", RemoteMode.Standard, 0, [Library, Shows], null, null);

    private static StubPlan Plan(
        IReadOnlyList<ResolvedContent> contents,
        IReadOnlyList<ServerCatalog> catalogs,
        StubState? state = null,
        IReadOnlyList<RemoteServerInfo>? servers = null,
        LocalSeries? localSeries = null)
    {
        var snapshot = new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = IndexHealth.Ok, Servers = catalogs };
        return StubPlanner.PlanByServer(new StubPlanInput(
            new FederationView(IndexHealth.Ok, 0, contents, []),
            snapshot,
            state ?? StubState.Empty,
            servers ?? [Server(_serverA, "Casa"), Server(_serverB, "Ufficio")],
            true,
            id => "http://127.0.0.1:8096/JellyMesh/Play/" + id,
            _ => localSeries));
    }

    private static StubWork On(StubPlan plan, Guid server) => Assert.Single(plan.Stubs, stub => stub.Entry.ServerId == server);

    private static ResolvedContent Local(ResolvedContent content, params Guid[] localIds) => content with { IsLocal = true, LocalItemIds = localIds };

    [Fact]
    public void TheBestCopyOfAServerIsItsStubAndItsOtherCopiesAreInternalSources()
    {
        var plan = Plan(
            [Title("k", OnA("m"), OnA("n"), OnB("z"))],
            [Catalog(_serverA, Film("m", "Film", Version("m720", 720), Version("m2160", 2160)), Film("n", "Film", Version("n1080"))), Catalog(_serverB, Film("z"))]);

        Assert.Equal(2, plan.Stubs.Count);
        Assert.Equal(
            [("m", "m2160"), ("n", "n1080"), ("m", "m720")],
            On(plan, _serverA).Entry.Sources.Select(source => (source.ItemId, source.MediaSourceId)));
        Assert.Equal(Library, On(plan, _serverA).Entry.Sources[0].LibraryId);
    }

    [Fact]
    public void AServerFolderGetsTheStubEvenWhenALocalCopyExists()
    {
        var plan = Plan([Local(Title("k", OnA("m")), Guid.NewGuid())], [Catalog(_serverA, Film("m"))]);

        Assert.Equal("m", Assert.Single(On(plan, _serverA).Entry.Sources).ItemId);
    }

    [Fact]
    public void AnEpisodeGoesInTheSeriesOfItsServerWithTheLabelAfterItsNumbers()
    {
        var plan = Plan(
            [EpisodeTitle(7, OnA("e7"))],
            [Catalog(_serverA, SeriesItem("sa"), EpisodeItem("e7", "sa", 7))],
            localSeries: new LocalSeries("Locale", 1999, null, new Dictionary<string, string>(), null, null));

        var series = Assert.Single(plan.Series);
        Assert.Equal(_serverA, series.Entry.ServerId);
        Assert.Equal(Path.Combine(A, "Serie", "Show (2020)"), series.Entry.RelativePath);
        Assert.Equal("Show", series.Metadata.Title);
        Assert.Equal(Path.Combine(A, "Serie", "Show (2020)", "Season 01", "Show S01E07 - Casa.strm"), On(plan, _serverA).Entry.RelativePath);
        Assert.Equal(series.Entry.Id, On(plan, _serverA).Entry.SeriesStubId);
    }

    [Fact]
    public void ATitleOnTwoServersGetsOneLabelledStubInTheFolderOfEach()
    {
        var plan = Plan([Title("k", OnA("m"), OnB("z"))], [Catalog(_serverA, Film("m")), Catalog(_serverB, Film("z"))]);

        Assert.Equal(2, plan.Stubs.Count);
        var onA = On(plan, _serverA);
        var onB = On(plan, _serverB);
        Assert.Equal(Path.Combine(A, "Film", "Film (2020)", "Film (2020) - Casa.strm"), onA.Entry.RelativePath);
        Assert.Equal(Path.Combine(B, "Film", "Film (2020)", "Film (2020) - Ufficio.strm"), onB.Entry.RelativePath);
        Assert.Equal([(_serverA, "m")], onA.Entry.Sources.Select(source => (source.ServerId, source.ItemId)));
        Assert.Equal([(_serverB, "z")], onB.Entry.Sources.Select(source => (source.ServerId, source.ItemId)));
        Assert.Equal(Path.Combine(A, "Film", "Film (2020)", "movie.nfo"), onA.NfoPath);
        Assert.NotEqual(onA.Entry.Id, onB.Entry.Id);
    }

    [Fact]
    public void PlanningAgainKeepsEveryStubWithItsIdAndPath()
    {
        var contents = new[] { Title("k", OnA("m"), OnB("z")) };
        var catalogs = new[] { Catalog(_serverA, Film("m")), Catalog(_serverB, Film("z")) };
        var first = Plan(contents, catalogs);

        var second = Plan(contents, catalogs, first.State);

        Assert.All(second.Stubs, stub => Assert.False(stub.IsNew));
        Assert.Equal(
            first.Stubs.Select(stub => (stub.Entry.Id, stub.Entry.RelativePath)).Order(),
            second.Stubs.Select(stub => (stub.Entry.Id, stub.Entry.RelativePath)).Order());
        Assert.Equal(2, second.State.Stubs.Count);
    }

    [Fact]
    public void AServerRemovedByTheAdministratorLosesItsStubs()
    {
        var first = Plan([Title("k", OnA("m"), OnB("z"))], [Catalog(_serverA, Film("m")), Catalog(_serverB, Film("z"))]);
        var onB = On(first, _serverB).Entry;

        var second = Plan(
            [Title("k", OnA("m"))],
            [Catalog(_serverA, Film("m"))],
            first.State with { RemovedServers = [_serverB] },
            [Server(_serverA, "Casa")]);

        Assert.Equal(onB.Id, Assert.Single(second.Removals).Id);
        Assert.Equal(0, second.Unconfigured);
    }

    [Fact]
    public void AServerMissingFromTheSettingsKeepsItsStubs()
    {
        var first = Plan([Title("k", OnA("m"), OnB("z"))], [Catalog(_serverA, Film("m")), Catalog(_serverB, Film("z"))]);
        var onB = On(first, _serverB).Entry;

        var second = Plan([Title("k", OnA("m"))], [Catalog(_serverA, Film("m"))], first.State, [Server(_serverA, "Casa")]);

        Assert.Empty(second.Removals);
        Assert.Equal(1, second.Unconfigured);
        Assert.Contains(second.State.Stubs, stub => stub.Id == onB.Id);
    }

    [Fact]
    public void AServerThatNoLongerHasATitleLosesItsStubThere()
    {
        var first = Plan([Title("k", OnA("m"), OnB("z"))], [Catalog(_serverA, Film("m")), Catalog(_serverB, Film("z"))]);
        var onB = On(first, _serverB).Entry;

        var second = Plan([Title("k", OnA("m"))], [Catalog(_serverA, Film("m")), Catalog(_serverB, Film("y", "Altro"))], first.State);

        Assert.Equal(onB.Id, Assert.Single(second.Removals).Id);
        Assert.Equal(On(first, _serverA).Entry.Id, On(second, _serverA).Entry.Id);
    }

    [Fact]
    public void CopiesMergedByHandOnOneServerBecomeVersionsOfOneStub()
    {
        var catalogs = new[] { Catalog(_serverA, Film("m"), Film("n", "Altro")) };
        var first = Plan([Title("k1", OnA("m")), Title("k2", OnA("n"))], catalogs);
        var kept = first.Stubs.Single(stub => stub.Entry.IdentityKey == "k1").Entry;

        var second = Plan([Title("k1", OnA("m"), OnA("n"))], catalogs, first.State);

        var stub = On(second, _serverA).Entry;
        Assert.Equal(kept.Id, stub.Id);
        Assert.Equal(["m", "n"], stub.Sources.Select(source => source.ItemId).Order());
        Assert.Equal(1, second.MergedStubs);
        Assert.Empty(second.Removals);
    }

    [Fact]
    public void StubsOfTheFlatLayoutAreLeftAsTheyAre()
    {
        var flat = new StubEntry
        {
            Id = "flat",
            Kind = StubKind.Movie,
            RelativePath = Path.Combine("Film", "Film (2020)", "Film (2020).strm"),
            IdentityKey = "k",
            Sources = [new StubSource(_serverA, "m", "m") { LibraryId = Library }]
        };

        var plan = Plan([Title("k", OnA("m"))], [Catalog(_serverA, Film("m"))], StubState.Empty with { Stubs = [flat] });

        Assert.NotEqual("flat", On(plan, _serverA).Entry.Id);
        Assert.Contains(flat, plan.State.Stubs);
        Assert.Empty(plan.Removals);
        Assert.Equal(0, plan.MergedStubs);
    }

    [Fact]
    public void TheFlatPlanLeavesTheStubsOfTheServerFoldersAsTheyAre()
    {
        var catalogs = new[] { Catalog(_serverA, Film("m")) };
        var byServer = Plan([Title("k", OnA("m"))], catalogs);
        var winner = new ResolvedSource(_serverA, "Casa", ServerSyncState.Complete, "m", "m", "Film", new SourceQuality(1080, false, 1000, "h264"), _now);

        var flat = StubPlanner.Plan(new StubPlanInput(
            new FederationView(IndexHealth.Ok, 0, [Title("k", OnA("m")) with { Winner = winner }], []),
            new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = IndexHealth.Ok, Servers = catalogs },
            byServer.State,
            [Server(_serverA, "Casa")],
            true,
            id => "http://127.0.0.1:8096/JellyMesh/Play/" + id,
            _ => null));

        var work = Assert.Single(flat.Stubs);
        Assert.True(work.IsNew);
        Assert.Null(work.Entry.ServerId);
        Assert.Contains(On(byServer, _serverA).Entry, flat.State.Stubs);
        Assert.Empty(flat.Removals);
    }
}
