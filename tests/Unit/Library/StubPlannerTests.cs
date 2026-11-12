using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class StubPlannerTests
{
    private const string Library = "films";

    private static readonly Guid _serverA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid _serverB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");
    private static readonly DateTimeOffset _now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    private static IndexedItem Film(string id, string name = "Film", int? year = 2020, string library = Library, IReadOnlyDictionary<string, string>? ids = null) => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Movie,
        LibraryId = library,
        LastSeenUtc = _now,
        Name = name,
        ProductionYear = year,
        ProviderIds = ids ?? new Dictionary<string, string> { ["Tmdb"] = "1" },
        Etag = "e1",
        ImageTags = new Dictionary<string, string> { ["Primary"] = "tag" },
        Versions = [new IndexedVersion { MediaSourceId = id }]
    };

    private static IndexedItem SeriesItem(string id, string name = "Show", IReadOnlyDictionary<string, string>? ids = null) => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Series,
        LibraryId = "shows",
        LastSeenUtc = _now,
        Name = name,
        ProductionYear = 2020,
        ProviderIds = ids ?? new Dictionary<string, string> { ["Tmdb"] = "9", ["Tvdb"] = "8", ["Imdb"] = "tt9" }
    };

    private static IndexedItem EpisodeItem(string id, string seriesId, int season, int number) => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Episode,
        LibraryId = "shows",
        LastSeenUtc = _now,
        Name = "Episode",
        SeriesId = seriesId,
        SeriesName = "Show",
        ParentIndexNumber = season,
        IndexNumber = number,
        Versions = [new IndexedVersion { MediaSourceId = id }]
    };

    private static ResolvedSource Source(Guid server, string itemId) => new(
        server,
        "Server",
        ServerSyncState.Complete,
        itemId,
        itemId,
        null,
        new SourceQuality(1080, false, 1000, "h264"),
        _now);

    private static ResolvedContent Film(string key, ResolvedSource winner, params ResolvedSource[] backups) => new(
        new ContentIdentity(key, key, ContentKind.Movie, MatchKind.ProviderId),
        false,
        winner,
        backups)
    {
        RemoteItems = backups.Prepend(winner).Select(source => new ResolvedItem(source.ServerId, source.ItemId)).ToList()
    };

    private static ResolvedContent Episode(string workKey, int season, int number, ResolvedSource winner) => new(
        new ContentIdentity(workKey + "|S" + season + "E" + number, workKey, ContentKind.Episode, MatchKind.ProviderId, season, number),
        false,
        winner,
        [])
    {
        RemoteItems = [new ResolvedItem(winner.ServerId, winner.ItemId)]
    };

    private static ResolvedContent Covering(ResolvedContent content, params ResolvedItem[] items) => content with
    {
        RemoteItems = content.RemoteItems.Concat(items).Distinct().ToList()
    };

    private static StubEntry ExistingEpisode(string id, string path, string key, int number, int? end, string itemId, string seriesStub) => new()
    {
        Id = id,
        Kind = StubKind.Episode,
        RelativePath = path,
        IdentityKey = key,
        Sources = [new StubSource(_serverA, itemId, itemId) { LibraryId = "shows" }],
        SeasonNumber = 1,
        EpisodeNumber = number,
        EpisodeNumberEnd = end,
        SeriesStubId = seriesStub,
        TechnicalTag = "tag"
    };

    private static ServerCatalog Catalog(Guid server, ServerSyncState state, params IndexedItem[] items) => new()
    {
        ServerId = server,
        State = state,
        Items = items
    };

    private static RemoteServerInfo Server(Guid id, params string[] libraries)
        => new(id, "Server", "http://server", RemoteMode.Standard, 0, libraries, null, null);

    private static StubPlan Plan(
        IReadOnlyList<ResolvedContent> contents,
        IReadOnlyList<ServerCatalog> catalogs,
        StubState? state = null,
        IReadOnlyList<RemoteServerInfo>? servers = null,
        IndexHealth health = IndexHealth.Ok,
        bool isProtected = true,
        Func<string, LocalSeries?>? localSeries = null,
        Func<string, string>? url = null)
    {
        var snapshot = new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = health, Servers = catalogs };
        return StubPlanner.Plan(new StubPlanInput(
            new FederationView(health, 0, contents, []),
            snapshot,
            state ?? StubState.Empty,
            servers ?? [Server(_serverA, Library, "shows"), Server(_serverB, Library, "shows")],
            isProtected,
            url ?? (id => "http://127.0.0.1:8096/JellyMesh/Play/" + id),
            localSeries ?? (_ => null)));
    }

    private static StubEntry ExistingStub(string id, string key, Guid server, string itemId, string library = Library) => new()
    {
        Id = id,
        Kind = StubKind.Movie,
        RelativePath = Path.Combine("Film", "Old Name (2020)", "Old Name (2020).strm"),
        IdentityKey = key,
        Sources = [new StubSource(server, itemId, itemId) { LibraryId = library }]
    };

    [Fact]
    public void AWinnerGetsAStubWhosePathDoesNotDependOnTheServer()
    {
        var onA = Plan([Film("k", Source(_serverA, "m"))], [Catalog(_serverA, ServerSyncState.Complete, Film("m"))]);
        var onB = Plan([Film("k", Source(_serverB, "z"))], [Catalog(_serverB, ServerSyncState.Complete, Film("z"))]);

        var stub = Assert.Single(onA.Stubs);
        Assert.True(stub.IsNew);
        Assert.Equal(Path.Combine("Film", "Film (2020)", "Film (2020).strm"), stub.Entry.RelativePath);
        Assert.Equal(stub.Entry.RelativePath, Assert.Single(onB.Stubs).Entry.RelativePath);
    }

    [Fact]
    public void TheStrmUrlHoldsNothingAboutTheRemote()
    {
        var plan = Plan([Film("k", Source(_serverA, "m"))], [Catalog(_serverA, ServerSyncState.Complete, Film("m"))]);

        var stub = Assert.Single(plan.Stubs);
        Assert.Equal("http://127.0.0.1:8096/JellyMesh/Play/" + stub.Entry.Id, stub.StrmUrl);
        Assert.DoesNotContain("aaaaaaaa", stub.StrmUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void ASignatureIsNoChangeOfContent()
    {
        var contents = new[] { Film("k", Source(_serverA, "m")) };
        var catalogs = new[] { Catalog(_serverA, ServerSyncState.Complete, Film("m")) };
        var unsigned = Assert.Single(Plan(contents, catalogs).Stubs);
        var state = StubState.Empty with { Stubs = [unsigned.Entry] };

        var signed = Assert.Single(Plan(contents, catalogs, state, url: id => "http://127.0.0.1:8096/JellyMesh/Play/" + id + "?sig=aaaa").Stubs);

        Assert.Equal(unsigned.ContentHash, signed.ContentHash);
        Assert.EndsWith("?sig=aaaa", signed.StrmUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewPortIsAChangeOfContent()
    {
        var contents = new[] { Film("k", Source(_serverA, "m")) };
        var catalogs = new[] { Catalog(_serverA, ServerSyncState.Complete, Film("m")) };
        var first = Assert.Single(Plan(contents, catalogs).Stubs);
        var state = StubState.Empty with { Stubs = [first.Entry] };

        var moved = Assert.Single(Plan(contents, catalogs, state, url: id => "http://127.0.0.1:9000/JellyMesh/Play/" + id).Stubs);

        Assert.NotEqual(first.ContentHash, moved.ContentHash);
    }

    [Fact]
    public void SourcesAreWinnerFirstThenBackups()
    {
        var plan = Plan(
            [Film("k", Source(_serverA, "m"), Source(_serverB, "z"))],
            [Catalog(_serverA, ServerSyncState.Complete, Film("m")), Catalog(_serverB, ServerSyncState.Complete, Film("z"))]);

        var sources = Assert.Single(plan.Stubs).Entry.Sources;
        Assert.Equal([(_serverA, "m"), (_serverB, "z")], sources.Select(source => (source.ServerId, source.ItemId)));
        Assert.All(sources, source => Assert.Equal(Library, source.LibraryId));
    }

    [Fact]
    public void AChangedKeyKeepsTheStubThroughItsRemoteItem()
    {
        var existing = ExistingStub("stub1", "movie|title:film|2020", _serverA, "m");

        var plan = Plan(
            [Film("movie|tmdb:1", Source(_serverA, "m"))],
            [Catalog(_serverA, ServerSyncState.Complete, Film("m"))],
            new StubState { Stubs = [existing] });

        var stub = Assert.Single(plan.Stubs);
        Assert.False(stub.IsNew);
        Assert.Equal("stub1", stub.Entry.Id);
        Assert.Equal(existing.RelativePath, stub.Entry.RelativePath);
        Assert.Equal("movie|tmdb:1", stub.Entry.IdentityKey);
        Assert.Empty(plan.Removals);
    }

    [Fact]
    public void TwoStubsThatBecameOneTitle_BothStayForThePostScanTask()
    {
        var first = ExistingStub("stub1", "movie|tmdb:1", _serverA, "m");
        var second = ExistingStub("stub2", "movie|tmdb:2", _serverB, "z") with { RelativePath = Path.Combine("Film", "Other", "Other.strm") };

        var plan = Plan(
            [Film("movie|tmdb:1", Source(_serverA, "m"), Source(_serverB, "z"))],
            [Catalog(_serverA, ServerSyncState.Complete, Film("m")), Catalog(_serverB, ServerSyncState.Complete, Film("z"))],
            new StubState { Stubs = [first, second] },
            [Server(_serverA, Library), Server(_serverB, Library)]);

        Assert.Equal("stub1", Assert.Single(plan.Stubs).Entry.Id);
        Assert.Empty(plan.Removals);
        Assert.Equal(1, plan.MergedStubs);
        Assert.Equal(2, plan.State.Stubs.Count);
    }

    [Fact]
    public void ATitleThatSplits_KeepsItsStubForOneCopyAndMintsOneForTheOther()
    {
        // Two copies merged on title and year alone before decision 13. Their stub stays, with one of them.
        var existing = ExistingStub("stub1", "movie|title:film|2020", _serverA, "m") with
        {
            Sources = [new StubSource(_serverA, "m", "m") { LibraryId = Library }, new StubSource(_serverB, "z", "z") { LibraryId = Library }]
        };

        var plan = Plan(
            [
                Film("movie|title:film|2020|a", Source(_serverA, "m")),
                Film("movie|title:film|2020|b", Source(_serverB, "z"))
            ],
            [Catalog(_serverA, ServerSyncState.Complete, Film("m")), Catalog(_serverB, ServerSyncState.Complete, Film("z"))],
            new StubState { Stubs = [existing] });

        Assert.Equal(2, plan.Stubs.Count);
        Assert.Equal(existing.RelativePath, plan.Stubs.Single(stub => !stub.IsNew).Entry.RelativePath);
        Assert.NotEqual(existing.RelativePath, plan.Stubs.Single(stub => stub.IsNew).Entry.RelativePath);
        Assert.Empty(plan.Removals);
        Assert.Equal(0, plan.MergedStubs);
    }

    [Fact]
    public void ANewWinnerKeepsThePathAndTheId()
    {
        var existing = ExistingStub("stub1", "k", _serverA, "m");

        var plan = Plan(
            [Film("k", Source(_serverB, "z"), Source(_serverA, "m"))],
            [Catalog(_serverA, ServerSyncState.Complete, Film("m")), Catalog(_serverB, ServerSyncState.Complete, Film("z", "Other Name"))],
            new StubState { Stubs = [existing] });

        var stub = Assert.Single(plan.Stubs);
        Assert.Equal("stub1", stub.Entry.Id);
        Assert.Equal(existing.RelativePath, stub.Entry.RelativePath);
        Assert.Equal(_serverB, stub.Entry.Sources[0].ServerId);
    }

    [Fact]
    public void AStubTurningLocalIsLeftToThePostScanTask()
    {
        var existing = ExistingStub("stub1", "k", _serverA, "m");
        var local = new ResolvedContent(new ContentIdentity("other key", "other key", ContentKind.Movie, MatchKind.ProviderId), true, null, [])
        {
            LocalItemIds = [Guid.NewGuid()],
            RemoteItems = [new ResolvedItem(_serverA, "m")]
        };

        var plan = Plan([local], [Catalog(_serverA, ServerSyncState.Complete, Film("m"))], new StubState { Stubs = [existing] });

        Assert.Empty(plan.Stubs);
        Assert.Empty(plan.Removals);
        Assert.Equal(existing, Assert.Single(plan.State.Stubs));
    }

    [Fact]
    public void ATitleGoneFromACompleteReadIsRemoved()
    {
        var existing = ExistingStub("stub1", "k", _serverA, "m");

        var plan = Plan([], [Catalog(_serverA, ServerSyncState.Complete, Film("other"))], new StubState { Stubs = [existing] });

        Assert.Equal(existing, Assert.Single(plan.Removals));
    }

    [Fact]
    public void ATitleMissingFromAStaleServerStays()
    {
        var existing = ExistingStub("stub1", "k", _serverA, "m");

        var plan = Plan([], [Catalog(_serverA, ServerSyncState.Stale, Film("other"))], new StubState { Stubs = [existing] });

        Assert.Empty(plan.Removals);
    }

    [Fact]
    public void AServerThatNeverSyncedProvesNothing()
    {
        var plan = Plan([], [], new StubState { Stubs = [ExistingStub("stub1", "k", _serverA, "m")] });

        Assert.Empty(plan.Removals);
    }

    [Theory]
    [InlineData(IndexHealth.Corrupt)]
    [InlineData(IndexHealth.RestoredFromBackup)]
    public void ADamagedIndexRemovesNothing(IndexHealth health)
    {
        var plan = Plan([], [Catalog(_serverA, ServerSyncState.Complete)], new StubState { Stubs = [ExistingStub("stub1", "k", _serverA, "m")] }, health: health);

        Assert.Empty(plan.Removals);
    }

    [Theory]
    [InlineData(IndexHealth.Ok)]
    [InlineData(IndexHealth.Corrupt)]
    [InlineData(IndexHealth.RestoredFromBackup)]
    public void AStubOfRemovedServersIsGoneWhateverTheIndexSays(IndexHealth health)
    {
        var state = new StubState { Stubs = [ExistingStub("stub1", "k", _serverA, "m")], RemovedServers = [_serverA] };

        var plan = Plan([], [], state, [Server(_serverB, Library)], health);

        Assert.Single(plan.Removals);
        Assert.Equal(0, plan.Unconfigured);
    }

    [Theory]
    [InlineData(IndexHealth.Ok)]
    [InlineData(IndexHealth.Corrupt)]
    public void AStubOfServersMissingFromTheConfigurationStaysAndIsCounted(IndexHealth health)
    {
        var existing = ExistingStub("stub1", "k", _serverA, "m");

        var plan = Plan([], [Catalog(_serverA, ServerSyncState.Complete)], new StubState { Stubs = [existing] }, [], health);

        Assert.Empty(plan.Removals);
        Assert.Equal(1, plan.Unconfigured);
        Assert.Equal(existing, Assert.Single(plan.State.Stubs));
    }

    [Fact]
    public void AStubWithOneRemovedAndOneUnknownServerStays()
    {
        var existing = ExistingStub("stub1", "k", _serverA, "m") with
        {
            Sources = [new StubSource(_serverA, "m", "m") { LibraryId = Library }, new StubSource(_serverB, "z", "z") { LibraryId = Library }]
        };

        var plan = Plan([], [], new StubState { Stubs = [existing], RemovedServers = [_serverA] }, []);

        Assert.Empty(plan.Removals);
        Assert.Equal(1, plan.Unconfigured);
    }

    [Fact]
    public void AMarkOnAServerStillConfiguredRemovesNothing()
    {
        var state = new StubState { Stubs = [ExistingStub("stub1", "k", _serverA, "m")], RemovedServers = [_serverA] };

        var plan = Plan([], [Catalog(_serverA, ServerSyncState.Stale)], state);

        Assert.Empty(plan.Removals);
        Assert.Equal(0, plan.Unconfigured);
    }

    [Fact]
    public void ThePlanKeepsTheRemovedServers()
    {
        var state = new StubState { Stubs = [ExistingStub("stub1", "k", _serverA, "m")], RemovedServers = [_serverA] };

        var plan = Plan([Film("k", Source(_serverB, "z"))], [Catalog(_serverB, ServerSyncState.Complete, Film("z"))], state, [Server(_serverB, Library)]);

        Assert.Equal([_serverA], plan.State.RemovedServers);
    }

    [Fact]
    public void AMarkOnAServerConfiguredAgainIsDropped()
    {
        var state = new StubState { Stubs = [ExistingStub("stub1", "k", _serverA, "m")], RemovedServers = [_serverA, _serverB] };

        var plan = Plan([], [Catalog(_serverA, ServerSyncState.Stale)], state, [Server(_serverA, Library)]);

        Assert.Equal([_serverB], plan.State.RemovedServers);
        Assert.Empty(plan.Removals);
    }

    [Fact]
    public void ADamagedIndexKeepsAStubWithASourceStillConfigured()
    {
        var existing = ExistingStub("stub1", "k", _serverA, "m") with
        {
            Sources = [new StubSource(_serverA, "m", "m") { LibraryId = Library }, new StubSource(_serverB, "z", "z") { LibraryId = Library }]
        };

        var plan = Plan([], [], new StubState { Stubs = [existing] }, [Server(_serverB, Library)], IndexHealth.Corrupt);

        Assert.Empty(plan.Removals);
    }

    [Fact]
    public void OneStaleSourceKeepsAStubOtherwiseGone()
    {
        var existing = ExistingStub("stub1", "k", _serverA, "m") with
        {
            Sources = [new StubSource(_serverA, "m", "m") { LibraryId = Library }, new StubSource(_serverB, "z", "z") { LibraryId = Library }]
        };

        var plan = Plan(
            [],
            [Catalog(_serverA, ServerSyncState.Complete, Film("other")), Catalog(_serverB, ServerSyncState.Stale, Film("z"))],
            new StubState { Stubs = [existing] });

        Assert.Empty(plan.Removals);
    }

    [Fact]
    public void AnEmptyCompleteReadHoldsTheStubsAndSaysSo()
    {
        var plan = Plan(
            [],
            [Catalog(_serverA, ServerSyncState.Complete)],
            new StubState { Stubs = [ExistingStub("s1", "k1", _serverA, "m1"), ExistingStub("s2", "k2", _serverA, "m2")] });

        Assert.Empty(plan.Removals);
        var held = Assert.Single(plan.Held);
        Assert.Equal((_serverA, Library, 2), (held.ServerId, held.LibraryId, held.Stubs));
        Assert.Equal(2, plan.State.Stubs.Count);
    }

    [Fact]
    public void OneEmptyLibraryIsHeldEvenIfAnotherOneHasItems()
    {
        var plan = Plan(
            [],
            [Catalog(_serverA, ServerSyncState.Complete, EpisodeItem("e", "s", 1, 1))],
            new StubState { Stubs = [ExistingStub("s1", "k1", _serverA, "m1")] });

        Assert.Empty(plan.Removals);
        Assert.Equal(Library, Assert.Single(plan.Held).LibraryId);
    }

    [Fact]
    public void ADeselectedLibraryIsNotHeld()
    {
        var plan = Plan(
            [],
            [Catalog(_serverA, ServerSyncState.Complete)],
            new StubState { Stubs = [ExistingStub("s1", "k1", _serverA, "m1")] },
            [Server(_serverA, "shows")]);

        Assert.Single(plan.Removals);
        Assert.Empty(plan.Held);
    }

    [Fact]
    public void AnEmptyLibraryNextToAServerNotReadInFullKeepsTheStubAndHoldsNothing()
    {
        var existing = ExistingStub("s1", "k1", _serverA, "m1") with
        {
            Sources = [new StubSource(_serverA, "m1", "m1") { LibraryId = Library }, new StubSource(_serverB, "z1", "z1") { LibraryId = Library }]
        };

        var plan = Plan([], [Catalog(_serverA, ServerSyncState.Complete), Catalog(_serverB, ServerSyncState.Stale)], new StubState { Stubs = [existing] });

        Assert.Empty(plan.Removals);
        Assert.Empty(plan.Held);
    }

    [Fact]
    public void TwoSourcesInOneEmptyLibraryHoldTheStubOnce()
    {
        var existing = ExistingStub("s1", "k1", _serverA, "m1") with
        {
            Sources = [new StubSource(_serverA, "m1", "m1") { LibraryId = Library }, new StubSource(_serverA, "m2", "m2") { LibraryId = Library }]
        };

        var plan = Plan([], [Catalog(_serverA, ServerSyncState.Complete)], new StubState { Stubs = [existing] });

        Assert.Empty(plan.Removals);
        var held = Assert.Single(plan.Held);
        Assert.Equal((_serverA, Library, 1), (held.ServerId, held.LibraryId, held.Stubs));
    }

    [Fact]
    public void EmptyLibrariesOnTwoServersAreEachHeld()
    {
        var existing = ExistingStub("s1", "k1", _serverA, "m1") with
        {
            Sources = [new StubSource(_serverA, "m1", "m1") { LibraryId = Library }, new StubSource(_serverB, "z1", "z1") { LibraryId = Library }]
        };

        var plan = Plan([], [Catalog(_serverA, ServerSyncState.Complete), Catalog(_serverB, ServerSyncState.Complete)], new StubState { Stubs = [existing] });

        Assert.Empty(plan.Removals);
        Assert.Equal([(_serverA, 1), (_serverB, 1)], plan.Held.Select(held => (held.ServerId, held.Stubs)).OrderBy(held => held.ServerId));
    }

    [Fact]
    public void ASourceWithNoLibraryIsNotHeld()
    {
        var existing = ExistingStub("s1", "k1", _serverA, "m1") with { Sources = [new StubSource(_serverA, "m1", "m1")] };

        var plan = Plan([], [Catalog(_serverA, ServerSyncState.Complete)], new StubState { Stubs = [existing] });

        Assert.Single(plan.Removals);
        Assert.Empty(plan.Held);
    }

    [Fact]
    public void AServerTheAdministratorRemovedReleasesItsStubs()
    {
        var plan = Plan(
            [],
            [Catalog(_serverA, ServerSyncState.Complete)],
            new StubState { Stubs = [ExistingStub("s1", "k1", _serverA, "m1")], RemovedServers = [_serverA] },
            []);

        Assert.Single(plan.Removals);
    }

    [Fact]
    public void TwoTitlesWithTheSameFolderNameDoNotShareIt()
    {
        var plan = Plan(
            [Film("k1", Source(_serverA, "a")), Film("k2", Source(_serverA, "b"))],
            [Catalog(_serverA, ServerSyncState.Complete, Film("a", "Same"), Film("b", "Same"))]);

        Assert.Equal(2, plan.Stubs.Select(stub => stub.Entry.RelativePath).Distinct().Count());
    }

    [Fact]
    public void EpisodesOfASeriesShareOneStubSeriesThatNeedsARefresh()
    {
        var plan = Plan(
            [Episode("series|tvdb:8", 1, 1, Source(_serverA, "e1")), Episode("series|tvdb:8", 1, 2, Source(_serverA, "e2"))],
            [Catalog(_serverA, ServerSyncState.Complete, SeriesItem("s"), EpisodeItem("e1", "s", 1, 1), EpisodeItem("e2", "s", 1, 2))]);

        var series = Assert.Single(plan.Series);
        Assert.True(series.Entry.NeedsRefresh);
        Assert.Equal(Path.Combine("Serie", "Show (2020)"), series.Entry.RelativePath);
        Assert.All(plan.Stubs, stub => Assert.Equal(series.Entry.Id, stub.Entry.SeriesStubId));
        Assert.Equal(
            Path.Combine("Serie", "Show (2020)", "Season 01", "Show S01E02.strm"),
            plan.Stubs[1].Entry.RelativePath);
        Assert.Equal((1, 2), (plan.Stubs[1].Entry.SeasonNumber, plan.Stubs[1].Entry.EpisodeNumber));
    }

    [Fact]
    public void ADoubleWinningBothEpisodes_IsOneStubWithARange()
    {
        var plan = Plan(
            [Episode("w", 1, 1, Source(_serverA, "d")), Episode("w", 1, 2, Source(_serverA, "d"))],
            [Catalog(_serverA, ServerSyncState.Complete, SeriesItem("s"), EpisodeItem("d", "s", 1, 1) with { IndexNumberEnd = 2 })]);

        var stub = Assert.Single(plan.Stubs);
        Assert.Equal((1, 2), (stub.Entry.EpisodeNumber, stub.Entry.EpisodeNumberEnd));
        Assert.Equal(Path.Combine("Serie", "Show (2020)", "Season 01", "Show S01E01-E02.strm"), stub.Entry.RelativePath);
        Assert.Equal(2, stub.Metadata.EpisodeNumberEnd);
        Assert.Contains("<episodenumberend>2</episodenumberend>", stub.Nfo, StringComparison.Ordinal);
    }

    [Fact]
    public void ADoubleWinningOneEpisode_IsAPlainStubOfThatEpisode()
    {
        var local = new ResolvedContent(new ContentIdentity("w|S1E1", "w", ContentKind.Episode, MatchKind.ProviderId, 1, 1), true, null, [])
        {
            RemoteItems = [new ResolvedItem(_serverA, "d")]
        };

        var plan = Plan(
            [local, Episode("w", 1, 2, Source(_serverA, "d"))],
            [Catalog(_serverA, ServerSyncState.Complete, SeriesItem("s"), EpisodeItem("d", "s", 1, 1) with { IndexNumberEnd = 2 })]);

        var stub = Assert.Single(plan.Stubs);
        Assert.Equal((2, (int?)null), (stub.Entry.EpisodeNumber!.Value, stub.Entry.EpisodeNumberEnd));
        Assert.EndsWith("Show S01E02.strm", stub.Entry.RelativePath, StringComparison.Ordinal);
        Assert.Equal("d", Assert.Single(stub.Entry.Sources).ItemId);
    }

    [Fact]
    public void AStubThatComesToCoverARange_KeepsItsPathAndGetsItsNumbersAgain()
    {
        // Written before IndexNumberEnd was read: the double had a plain stub of its first episode.
        var existing = ExistingEpisode("stub1", Path.Combine("Serie", "Show (2020)", "Season 01", "Show S01E01.strm"), "w|S1E1", 1, null, "d", "series1");

        var plan = Plan(
            [Episode("w", 1, 1, Source(_serverA, "d")), Episode("w", 1, 2, Source(_serverA, "d"))],
            [Catalog(_serverA, ServerSyncState.Complete, SeriesItem("s"), EpisodeItem("d", "s", 1, 1) with { IndexNumberEnd = 2 })],
            new StubState { Stubs = [existing] });

        var stub = Assert.Single(plan.Stubs);
        Assert.False(stub.IsNew);
        Assert.Equal(existing.RelativePath, stub.Entry.RelativePath);
        Assert.Equal(2, stub.Entry.EpisodeNumberEnd);
        Assert.Null(stub.Entry.TechnicalTag);
        Assert.Equal("series1", stub.Entry.SeriesStubId);
    }

    [Fact]
    public void AStubThatKeepsItsNumbers_KeepsItsTechnicalTag()
    {
        var existing = ExistingEpisode("stub1", Path.Combine("Serie", "Show (2020)", "Season 01", "Show S01E01.strm"), "w|S1E1", 1, null, "e", "series1");

        var plan = Plan(
            [Episode("w", 1, 1, Source(_serverA, "e"))],
            [Catalog(_serverA, ServerSyncState.Complete, SeriesItem("s"), EpisodeItem("e", "s", 1, 1))],
            new StubState { Stubs = [existing] });

        Assert.Equal("tag", Assert.Single(plan.Stubs).Entry.TechnicalTag);
    }

    [Fact]
    public void WithOneEpisodeLocal_AStubOfARange_ShrinksToTheOtherAndStays()
    {
        var existing = ExistingEpisode("stub1", Path.Combine("Serie", "Show (2020)", "Season 01", "Show S01E01-E02.strm"), "w|S1E1", 1, 2, "d", "series1");
        var local = new ResolvedContent(new ContentIdentity("w|S1E1", "w", ContentKind.Episode, MatchKind.ProviderId, 1, 1), true, null, [])
        {
            RemoteItems = [new ResolvedItem(_serverA, "d")]
        };

        var plan = Plan(
            [local, Episode("w", 1, 2, Source(_serverA, "d"))],
            [Catalog(_serverA, ServerSyncState.Complete, SeriesItem("s"), EpisodeItem("d", "s", 1, 1) with { IndexNumberEnd = 2 })],
            new StubState { Stubs = [existing] });

        var stub = Assert.Single(plan.Stubs);
        Assert.Equal("stub1", stub.Entry.Id);
        Assert.Equal((2, (int?)null), (stub.Entry.EpisodeNumber!.Value, stub.Entry.EpisodeNumberEnd));
        Assert.Equal(existing.RelativePath, stub.Entry.RelativePath);
        Assert.Null(stub.Entry.TechnicalTag);
        Assert.Empty(plan.Removals);
    }

    [Fact]
    public void AStubOfARange_ThatLosesOneEpisodeToABetterCopy_ShrinksAndTheCopyGetsItsOwn()
    {
        var existing = ExistingEpisode("stub1", Path.Combine("Serie", "Show (2020)", "Season 01", "Show S01E01-E02.strm"), "w|S1E1", 1, 2, "d", "series1");

        var plan = Plan(
            [
                Episode("w", 1, 1, Source(_serverA, "d")),
                Covering(Episode("w", 1, 2, Source(_serverB, "better")), new ResolvedItem(_serverA, "d"))
            ],
            [
                Catalog(_serverA, ServerSyncState.Complete, SeriesItem("s"), EpisodeItem("d", "s", 1, 1) with { IndexNumberEnd = 2 }),
                Catalog(_serverB, ServerSyncState.Complete, SeriesItem("t"), EpisodeItem("better", "t", 1, 2))
            ],
            new StubState { Stubs = [existing] });

        Assert.Equal((1, (int?)null), (plan.Stubs.Single(stub => !stub.IsNew).Entry.EpisodeNumber!.Value, plan.Stubs.Single(stub => !stub.IsNew).Entry.EpisodeNumberEnd));
        Assert.EndsWith("Show S01E02.strm", plan.Stubs.Single(stub => stub.IsNew).Entry.RelativePath, StringComparison.Ordinal);
        Assert.Empty(plan.Removals);
    }

    [Fact]
    public void TwoStubsThatBecomeARange_KeepTheFirstAndMergeTheOther()
    {
        var folder = Path.Combine("Serie", "Show (2020)", "Season 01");
        var first = ExistingEpisode("stub1", Path.Combine(folder, "Show S01E01.strm"), "w|S1E1", 1, null, "e1", "series1");
        var second = ExistingEpisode("stub2", Path.Combine(folder, "Show S01E02.strm"), "w|S1E2", 2, null, "e2", "series1");

        var plan = Plan(
            [
                Covering(Episode("w", 1, 1, Source(_serverB, "d")), new ResolvedItem(_serverA, "e1")),
                Covering(Episode("w", 1, 2, Source(_serverB, "d")), new ResolvedItem(_serverA, "e2"))
            ],
            [
                Catalog(_serverA, ServerSyncState.Complete, SeriesItem("s"), EpisodeItem("e1", "s", 1, 1), EpisodeItem("e2", "s", 1, 2)),
                Catalog(_serverB, ServerSyncState.Complete, SeriesItem("t"), EpisodeItem("d", "t", 1, 1) with { IndexNumberEnd = 2 })
            ],
            new StubState { Stubs = [first, second] },
            [Server(_serverA, "shows"), Server(_serverB, "shows")]);

        var stub = Assert.Single(plan.Stubs);
        Assert.Equal("stub1", stub.Entry.Id);
        Assert.Equal(2, stub.Entry.EpisodeNumberEnd);
        Assert.Empty(plan.Removals);
        Assert.Equal(1, plan.MergedStubs);
    }

    [Fact]
    public void AnEpisodeWithoutNumbersHasNoPlace()
    {
        var content = new ResolvedContent(new ContentIdentity("x", "x", ContentKind.Episode, MatchKind.ItemOnly), false, Source(_serverA, "e"), []);

        var plan = Plan([content], [Catalog(_serverA, ServerSyncState.Complete, EpisodeItem("e", "s", 1, 1))]);

        Assert.Empty(plan.Stubs);
        Assert.Equal(1, plan.Unplaceable);
    }

    [Fact]
    public void AStubSeriesFollowsTheKeyAndTheTextOfTheLocalSeries()
    {
        var local = new LocalSeries(
            "Exact Local Name",
            2019,
            "Local plot",
            new Dictionary<string, string> { ["Tvdb"] = "8" },
            "/media/poster.jpg",
            null);

        var plan = Plan(
            [Episode("w", 1, 4, Source(_serverA, "e"))],
            [Catalog(_serverA, ServerSyncState.Complete, SeriesItem("s", "Remote Name"), EpisodeItem("e", "s", 1, 4))],
            localSeries: _ => local);

        var series = Assert.Single(plan.Series);
        Assert.Equal("Exact Local Name", series.Metadata.Title);
        Assert.Equal("Local plot", series.Metadata.Overview);
        Assert.Equal(2019, series.Metadata.Year);
        Assert.Equal(new Dictionary<string, string> { ["Tvdb"] = "8" }, series.Metadata.ProviderIds);
        Assert.Equal("/media/poster.jpg", Assert.Single(series.Images).LocalPath);
    }

    [Fact]
    public void AStubSeriesWithALocalOneTakesNoLogoBannerOrThumbFromTheRemote()
    {
        var local = new LocalSeries("Local", 2019, null, new Dictionary<string, string>(), "/media/poster.jpg", null);
        var art = new Dictionary<string, string> { ["Primary"] = "p", ["Logo"] = "l", ["Banner"] = "b", ["Thumb"] = "t" };

        var withLocal = Plan(
            [Episode("w", 1, 4, Source(_serverA, "e"))],
            [Catalog(_serverA, ServerSyncState.Complete, SeriesItem("s") with { ImageTags = art }, EpisodeItem("e", "s", 1, 4))],
            localSeries: _ => local);
        var alone = Plan(
            [Episode("w", 1, 4, Source(_serverA, "e"))],
            [Catalog(_serverA, ServerSyncState.Complete, SeriesItem("s") with { ImageTags = art }, EpisodeItem("e", "s", 1, 4))]);

        Assert.Equal([ImageType.Primary], Assert.Single(withLocal.Series).Images.Select(image => image.Type));
        Assert.Equal([ImageType.Primary, ImageType.Logo, ImageType.Banner, ImageType.Thumb], Assert.Single(alone.Series).Images.Select(image => image.Type));
    }

    [Fact]
    public void ALocalSeriesWithoutKeyIdsLeavesOnlyItsNameAsTheKey()
    {
        var local = new LocalSeries("Serie Senza ID", 2026, null, new Dictionary<string, string>(), null, null);

        var plan = Plan(
            [Episode("w", 1, 2, Source(_serverA, "e"))],
            [Catalog(_serverA, ServerSyncState.Complete, SeriesItem("s"), EpisodeItem("e", "s", 1, 2))],
            localSeries: _ => local);

        var metadata = Assert.Single(plan.Series).Metadata;
        Assert.Empty(metadata.ProviderIds);
        Assert.Equal("Serie Senza ID", metadata.Title);
    }

    [Fact]
    public void AStubSeriesWithoutALocalOneUsesTheRemoteSeries()
    {
        var plan = Plan(
            [Episode("w", 1, 2, Source(_serverA, "e"))],
            [Catalog(_serverA, ServerSyncState.Complete, SeriesItem("s", "Remote Name"), EpisodeItem("e", "s", 1, 2))]);

        var series = Assert.Single(plan.Series);
        Assert.Equal("Remote Name", series.Metadata.Title);
        Assert.Equal("8", series.Metadata.ProviderIds["Tvdb"]);
    }

    [Fact]
    public void AChangedRemoteMetadataChangesTheHash()
    {
        var before = Plan([Film("k", Source(_serverA, "m"))], [Catalog(_serverA, ServerSyncState.Complete, Film("m"))]);
        var after = Plan([Film("k", Source(_serverA, "m"))], [Catalog(_serverA, ServerSyncState.Complete, Film("m") with { Overview = "New" })]);

        Assert.NotEqual(Assert.Single(before.Stubs).ContentHash, Assert.Single(after.Stubs).ContentHash);
    }

    [Fact]
    public void AFilmWantsItsPosterAndBackdrop()
    {
        var film = Film("m") with { BackdropImageTags = ["b"] };

        var plan = Plan([Film("k", Source(_serverA, "m"))], [Catalog(_serverA, ServerSyncState.Complete, film)]);

        Assert.Equal([ImageType.Primary, ImageType.Backdrop], Assert.Single(plan.Stubs).Images.Select(image => image.Type));
    }
}
