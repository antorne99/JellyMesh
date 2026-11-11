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
        IReadOnlyList<RemoteServerInfo>? servers = null)
    {
        var snapshot = new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = IndexHealth.Ok, Servers = catalogs };
        return StubPlanner.PlanByServer(new StubPlanInput(
            new FederationView(IndexHealth.Ok, 0, contents, []),
            snapshot,
            state ?? StubState.Empty,
            servers ?? [Server(_serverA, "Casa"), Server(_serverB, "Ufficio")],
            true,
            id => "http://127.0.0.1:8096/JellyMesh/Play/" + id,
            _ => null));
    }

    private static StubWork On(StubPlan plan, Guid server) => Assert.Single(plan.Stubs, stub => stub.Entry.ServerId == server);

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
}
