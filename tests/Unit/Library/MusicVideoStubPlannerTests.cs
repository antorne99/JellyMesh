using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

/// <summary>
/// Row 454: a music video found on several servers is one title. Each server folder has its stub, and the stubs with the
/// local copy form a version group, as for a film. Home videos are never deduplicated.
/// </summary>
public class MusicVideoStubPlannerTests
{
    private const string MusicVideos = TestLibraries.MusicVideos;
    private const string HomeVideos = TestLibraries.HomeVideos;
    private const string Key = "musicvideo|imdb:tt1";

    private static readonly Guid _serverA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid _serverB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");
    private static readonly DateTimeOffset _now = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

    private static IndexedVersion Version(string id, int height = 1080) => new() { MediaSourceId = id, Height = height, Codec = "h264", BitRate = 1000, VideoRange = "SDR" };

    private static IndexedItem Clip(string id, int height = 1080) => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.MusicVideo,
        LibraryId = MusicVideos,
        LastSeenUtc = _now,
        Name = "Song",
        ProductionYear = 2019,
        Artists = ["Singer"],
        ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt1" },
        Versions = [Version(id, height)]
    };

    private static IndexedItem HomeVideo(string id) => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Video,
        LibraryId = HomeVideos,
        LastSeenUtc = _now,
        Name = "Beach day",
        ParentId = HomeVideos,
        Versions = [Version(id)]
    };

    private static ResolvedContent Title(params ResolvedItem[] items) => new(
        new ContentIdentity(Key, Key, ContentKind.MusicVideo, MatchKind.ProviderId),
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
        => new(id, name, "http://server", RemoteMode.Standard, 0, [MusicVideos, HomeVideos], null, null);

    private static StubPlanInput Input(IReadOnlyList<ResolvedContent> contents, IReadOnlyList<ServerCatalog> catalogs, StubState? state = null)
    {
        var snapshot = new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = IndexHealth.Ok, Servers = catalogs };
        return new StubPlanInput(
            new FederationView(IndexHealth.Ok, 0, contents, []),
            snapshot,
            state ?? StubState.Empty,
            [Server(_serverA, "Casa"), Server(_serverB, "Ufficio")],
            true,
            id => "http://127.0.0.1:8096/JellyMesh/Play/" + id,
            _ => null)
        {
            IgnoreRules = new FakeIgnoreRules()
        };
    }

    private static StubPlan Plan(IReadOnlyList<ResolvedContent> contents, IReadOnlyList<ServerCatalog> catalogs, StubState? state = null)
        => StubPlanner.PlanByServer(Input(contents, catalogs, state));

    private static string In(Guid server, params string[] parts) => Path.Combine(FederatedPaths.LibraryFolder(server, MusicVideos), Path.Combine(parts));

    [Fact]
    public void AMusicVideoOnTwoServersIsAStubInEachServerFolder_AndOneVersionGroup()
    {
        var plan = Plan([Title(OnA("m"), OnB("z"))], [Catalog(_serverA, Clip("m")), Catalog(_serverB, Clip("z", 720))]);

        Assert.Equal(2, plan.Stubs.Count);
        var onA = plan.Stubs.Single(work => work.Entry.ServerId == _serverA);
        var onB = plan.Stubs.Single(work => work.Entry.ServerId == _serverB);
        Assert.All(plan.Stubs, work => Assert.Equal(StubKind.MusicVideo, work.Entry.Kind));
        Assert.All(plan.Stubs, work => Assert.Equal(Key, work.Entry.IdentityKey));
        Assert.Equal(In(_serverA, "Video musicali", "Song (2019)", "Song (2019) - Casa.strm"), onA.Entry.RelativePath);
        Assert.Equal(In(_serverA, "Video musicali", "Song (2019)", "Song (2019) - Casa.nfo"), onA.NfoPath);
        Assert.Equal("musicvideo", XDocument.Parse(onA.Nfo).Root!.Name.LocalName);
        Assert.Equal("Singer", XDocument.Parse(onA.Nfo).Root!.Element("artist")?.Value);
        Assert.Equal([new StubSource(_serverB, "z", "z") { LibraryId = MusicVideos }], onB.Entry.Sources);

        var group = Assert.Single(plan.State.Groups);
        Assert.Equal(Key, group.IdentityKey);
        Assert.Equal(new[] { onA.Entry.Id, onB.Entry.Id }.Order(StringComparer.Ordinal), group.StubIds.Order(StringComparer.Ordinal));
        Assert.Empty(group.LocalItemIds);
    }

    [Fact]
    public void TheLocalCopyOfAMusicVideoJoinsTheGroup_AndTheStubsStay()
    {
        var local = Guid.NewGuid();
        var content = Title(OnA("m"), OnB("z")) with { IsLocal = true, LocalItemIds = [local] };

        var plan = Plan([content], [Catalog(_serverA, Clip("m")), Catalog(_serverB, Clip("z"))]);

        Assert.Equal(2, plan.Stubs.Count);
        var group = Assert.Single(plan.State.Groups);
        Assert.Equal([local], group.LocalItemIds);
        Assert.Equal(2, group.StubIds.Count);
    }

    [Fact]
    public void TwoCopiesOfAMusicVideoOnOneServerAreOneStubWithBothAsSources()
    {
        var plan = Plan([Title(OnA("m"), OnA("n"))], [Catalog(_serverA, Clip("m", 720), Clip("n"))]);

        var work = Assert.Single(plan.Stubs);
        Assert.Equal(["n", "m"], work.Entry.Sources.Select(source => source.ItemId));
    }

    [Fact]
    public void AMusicVideoStubOfOneRemoteItem_KeepsItsIdWhenItsTitleIsResolved()
    {
        var first = Plan([Title(OnA("m"))], [Catalog(_serverA, Clip("m"))]);
        var older = Assert.Single(first.Stubs).Entry with { IdentityKey = "MusicVideo|" + _serverA.ToString("N") + "|m" };

        var plan = Plan([Title(OnA("m"))], [Catalog(_serverA, Clip("m"))], first.State with { Stubs = [older], Groups = [] });

        var work = Assert.Single(plan.Stubs);
        Assert.Equal(older.Id, work.Entry.Id);
        Assert.Equal(older.RelativePath, work.Entry.RelativePath);
        Assert.Equal(Key, work.Entry.IdentityKey);
        Assert.Empty(plan.Removals);
    }

    [Fact]
    public void TheSameHomeVideoOnTwoServersIsTwoStubsInNoGroup()
    {
        var plan = Plan([], [Catalog(_serverA, HomeVideo("v")), Catalog(_serverB, HomeVideo("v"))]);

        Assert.Equal(2, plan.Stubs.Count);
        Assert.All(plan.Stubs, work => Assert.Equal(StubKind.HomeVideo, work.Entry.Kind));
        Assert.Empty(plan.State.Groups);
    }

    [Fact]
    public void TheOlderLayoutLeavesTheStubsOfMusicVideosAsTheyAre()
    {
        var stub = new StubEntry
        {
            Id = "clip",
            Kind = StubKind.MusicVideo,
            ServerId = _serverA,
            RelativePath = In(_serverA, "Video musicali", "Song (2019)", "Song (2019) - Casa.strm"),
            IdentityKey = "MusicVideo|" + _serverA.ToString("N") + "|m",
            Sources = [new StubSource(_serverA, "m", "m") { LibraryId = MusicVideos }]
        };

        var plan = StubPlanner.PlanByServer(Input([Title(OnA("m"))], [Catalog(_serverA, Clip("m"))], StubState.Empty with { Stubs = [stub] }) with { KeepsOlderLayout = true });

        Assert.Empty(plan.Stubs);
        Assert.Empty(plan.Removals);
        Assert.Equal([stub], plan.State.Stubs);
        Assert.Equal(0, plan.Unplaceable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMusicVideoGoesToTheRemoteLibraryLayoutBesideAStubOfAnOlderLayout(bool is05)
    {
        // §10 row 444: new titles go to the remote library layout while the migration empties the older one (Q49).
        var older = new StubEntry
        {
            Id = "old",
            Kind = StubKind.Movie,
            ServerId = is05 ? _serverA : null,
            RelativePath = is05 ? FederatedPaths.MovieStub(_serverA, "Film (2020)", "Casa") : FederatedPaths.MovieStub("Film (2020)"),
            IdentityKey = "film",
            Sources = []
        };

        var plan = Plan([Title(OnA("m"))], [Catalog(_serverA, Clip("m"))], StubState.Empty with { Stubs = [older] });

        Assert.Equal(In(_serverA, "Video musicali", "Song (2019)", "Song (2019) - Casa.strm"), Assert.Single(plan.Stubs).Entry.RelativePath);
        Assert.Contains(older, plan.State.Stubs);
        Assert.Empty(plan.Removals);
        Assert.Equal(0, plan.Unplaceable);
    }

    [Fact]
    public void TheFlatPlanMakesNoStubForAMusicVideo_AndCountsNothingUnplaceable()
    {
        var winner = new ResolvedSource(_serverA, "Casa", ServerSyncState.Complete, "m", "m", "Song", new SourceQuality(1080, false, 1000, "h264"), _now);

        var plan = StubPlanner.Plan(Input([Title(OnA("m")) with { Winner = winner }], [Catalog(_serverA, Clip("m"))]));

        Assert.Empty(plan.Stubs);
        Assert.Equal(0, plan.Unplaceable);
    }
}
