using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class TypeStubPlannerTests
{
    private const string MusicVideos = TestLibraries.MusicVideos;
    private const string HomeVideos = TestLibraries.HomeVideos;
    private const string Mixed = TestLibraries.Mixed;

    private static readonly Guid _serverA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly DateTimeOffset _now = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

    private static IndexedVersion Version(string id) => new() { MediaSourceId = id, Height = 1080, Codec = "h264", BitRate = 1000, VideoRange = "SDR" };

    private static IndexedItem MusicVideo(string id, string name = "Song") => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.MusicVideo,
        LibraryId = MusicVideos,
        LastSeenUtc = _now,
        Name = name,
        ProductionYear = 2019,
        Artists = ["Singer"],
        Album = "Album",
        ImageTags = new Dictionary<string, string> { ["Primary"] = "p1" },
        Versions = [Version(id)]
    };

    private static IndexedItem HomeVideo(string id, string name, string parentId) => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Video,
        LibraryId = HomeVideos,
        LastSeenUtc = _now,
        Name = name,
        ParentId = parentId,
        PremiereDate = new DateTimeOffset(2021, 7, 8, 0, 0, 0, TimeSpan.Zero),
        ImageTags = new Dictionary<string, string> { ["Primary"] = "p1" },
        Versions = [Version(id)]
    };

    private static IndexedItem Folder(string id, string name, string parentId) => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Folder,
        LibraryId = HomeVideos,
        LastSeenUtc = _now,
        Name = name,
        ParentId = parentId
    };

    private static ServerCatalog Catalog(params IndexedItem[] items) => new()
    {
        ServerId = _serverA,
        State = ServerSyncState.Complete,
        Items = items
    };

    private static RemoteServerInfo Server()
        => new(_serverA, "Casa", "http://server", RemoteMode.Standard, 0, [MusicVideos, HomeVideos, Mixed], null, null);

    private static StubPlan Plan(ServerCatalog catalog, StubState? state = null, IReadOnlyList<ResolvedContent>? contents = null, bool isProtected = true, IServerIgnoreRules? ignoreRules = null)
    {
        var snapshot = new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = IndexHealth.Ok, Servers = [catalog] };
        var input = new StubPlanInput(
            new FederationView(IndexHealth.Ok, 0, contents ?? [], []),
            snapshot,
            state ?? StubState.Empty,
            [Server()],
            isProtected,
            id => "http://127.0.0.1:8096/JellyMesh/Play/" + id,
            _ => null);
        return StubPlanner.PlanByServer(ignoreRules is null ? input : input with { IgnoreRules = ignoreRules });
    }

    private static string In(string library, params string[] parts) => Path.Combine(FederatedPaths.LibraryFolder(_serverA, library), Path.Combine(parts));

    [Fact]
    public void AMusicVideoGetsATitleFolderLikeAFilmWithANfoOfItsOwnName()
    {
        var work = Assert.Single(Plan(Catalog(MusicVideo("m1"))).Stubs);

        var stem = In(MusicVideos, "Video musicali", "Song (2019)", "Song (2019) - Casa");
        Assert.Equal(StubKind.MusicVideo, work.Entry.Kind);
        Assert.Equal(stem + ".strm", work.Entry.RelativePath);
        Assert.Equal(stem + ".nfo", work.NfoPath);
        Assert.Equal("musicvideo", XDocument.Parse(work.Nfo).Root!.Name.LocalName);
        Assert.Equal("Singer", XDocument.Parse(work.Nfo).Root!.Element("artist")?.Value);
        var poster = Assert.Single(work.Images);
        Assert.Equal((ImageType.Primary, In(MusicVideos, "Video musicali", "Song (2019)", "poster")), (poster.Type, poster.RelativeStem));
        Assert.Equal([new StubSource(_serverA, "m1", "m1") { LibraryId = MusicVideos }], work.Entry.Sources);
    }

    [Theory]
    [InlineData("Trailers")]
    [InlineData("Other")]
    public void AMusicVideoNamedLikeAnExtrasFolderGetsTheIdInItsFolder(string name)
    {
        Assert.True(JellyfinNaming.IsExtra(In(MusicVideos, "Video musicali", name, name + " - Casa.strm")));

        var work = Assert.Single(Plan(Catalog(MusicVideo("m1", name) with { ProductionYear = null })).Stubs);

        var folder = $"{name} [{work.Entry.Id[..8]}]";
        Assert.Equal(In(MusicVideos, "Video musicali", folder, folder + " - Casa.strm"), work.Entry.RelativePath);
        Assert.False(JellyfinNaming.IsExtra(work.Entry.RelativePath));
    }

    [Fact]
    public void TwoMusicVideosWithOneTitleGetTwoFolders()
    {
        var works = Plan(Catalog(MusicVideo("m1"), MusicVideo("m2"))).Stubs;

        Assert.Equal(2, works.Select(work => Path.GetDirectoryName(work.Entry.RelativePath)).Distinct().Count());
    }

    [Fact]
    public void AHomeVideoKeepsTheFoldersOfItsRemoteLibrary()
    {
        var plan = Plan(Catalog(Folder("f1", "Trips", HomeVideos), Folder("f2", "Sea", "f1"), HomeVideo("v1", "Beach day", "f2")));

        var work = Assert.Single(plan.Stubs);
        var stem = In(HomeVideos, "Video domestici", "Trips", "Sea", "Beach day");
        Assert.Equal(StubKind.HomeVideo, work.Entry.Kind);
        Assert.Equal(stem + ".strm", work.Entry.RelativePath);
        Assert.Equal(stem + ".nfo", work.NfoPath);
        Assert.Equal("movie", XDocument.Parse(work.Nfo).Root!.Name.LocalName);
        Assert.Equal("2021-07-08", XDocument.Parse(work.Nfo).Root!.Element("premiered")?.Value);
        var thumb = Assert.Single(work.Images);
        Assert.Equal((ImageType.Primary, stem + "-thumb"), (thumb.Type, thumb.RelativeStem));
    }

    [Fact]
    public void AHomeVideoAtTheTopOfItsLibraryHasNoFolder()
    {
        var work = Assert.Single(Plan(Catalog(HomeVideo("v1", "Beach day", HomeVideos))).Stubs);

        Assert.Equal(In(HomeVideos, "Video domestici", "Beach day.strm"), work.Entry.RelativePath);
    }

    [Fact]
    public void HomeVideosWithOneNameInAFolderAreNotCopiesAndGetTwoFiles()
    {
        var plan = Plan(Catalog(HomeVideo("v1", "Beach day", HomeVideos), HomeVideo("v2", "beach DAY", HomeVideos)));

        Assert.Equal(2, plan.Stubs.Count);
        Assert.Equal(2, plan.Stubs.Select(work => work.Entry.RelativePath.ToUpperInvariant()).Distinct().Count());
    }

    [Fact]
    public void AFolderNameCannotLeaveTheLibraryAndALoopOfParentsEnds()
    {
        var plan = Plan(Catalog(Folder("f1", "..", "f2"), Folder("f2", "Loop", "f1"), HomeVideo("v1", "Beach day", "f1")));

        var work = Assert.Single(plan.Stubs);
        Assert.StartsWith(In(HomeVideos, "Video domestici") + Path.DirectorySeparatorChar, work.Entry.RelativePath, StringComparison.Ordinal);
        Assert.DoesNotContain("..", work.Entry.RelativePath.Split(Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData("A-thumb", "A")]
    [InlineData("A", "A-thumb")]
    public void AHomeVideoNamedLikeTheThumbnailOfAnotherGetsADifferentName(string first, string second)
    {
        var stems = Plan(Catalog(HomeVideo("v1", first, HomeVideos), HomeVideo("v2", second, HomeVideos))).Stubs
            .Select(work => Path.GetFileNameWithoutExtension(work.Entry.RelativePath))
            .ToList();

        Assert.Equal(2, stems.Count);
        Assert.DoesNotContain(stems, stem => stems.Contains(stem + "-thumb", StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void AFolderNamedLikeTheFileOfAHomeVideoGetsADifferentName()
    {
        var plan = Plan(Catalog(Folder("f1", "Clip.strm", HomeVideos), HomeVideo("v1", "Clip", HomeVideos), HomeVideo("v2", "Inside", "f1")));

        var paths = plan.Stubs.Select(work => work.Entry.RelativePath).ToList();
        Assert.Contains(In(HomeVideos, "Video domestici", "Clip.strm"), paths);
        Assert.Contains(In(HomeVideos, "Video domestici", "Clip.strm [f1]", "Inside.strm"), paths);
    }

    [Theory]
    [InlineData("Beach-trailer")]
    [InlineData("Beach.trailer")]
    [InlineData("Beach_trailer2")]
    [InlineData("trailer")]
    [InlineData("Beach-sample")]
    [InlineData("sample")]
    [InlineData("Beach-featurette")]
    [InlineData("Beach-behindthescenes")]
    [InlineData("Beach-deleted")]
    [InlineData("Beach-deletedscene")]
    [InlineData("Beach-interview")]
    [InlineData("Beach-scene")]
    [InlineData("Beach-short")]
    [InlineData("Beach-clip")]
    [InlineData("Beach-other")]
    [InlineData("Beach-extra")]
    public void AHomeVideoNamedLikeAnExtraGetsANameReadAsAMainVideo(string name)
    {
        Assert.True(JellyfinNaming.IsExtra(In(HomeVideos, "Video domestici", name + ".strm")));

        var work = Assert.Single(Plan(Catalog(HomeVideo("v1", name, HomeVideos))).Stubs);

        Assert.StartsWith(name + " [", Path.GetFileName(work.Entry.RelativePath), StringComparison.Ordinal);
        Assert.False(JellyfinNaming.IsExtra(work.Entry.RelativePath));
    }

    [Theory]
    [InlineData("trailers")]
    [InlineData("extras")]
    [InlineData("behind the scenes")]
    [InlineData("deleted scenes")]
    [InlineData("interviews")]
    [InlineData("scenes")]
    [InlineData("samples")]
    [InlineData("shorts")]
    [InlineData("featurettes")]
    [InlineData("clips")]
    [InlineData("other")]
    [InlineData("theme-music")]
    [InlineData("backdrops")]
    [InlineData("Trailers")]
    public void AFolderNamedLikeAnExtrasFolderGetsANameReadAsAPlainFolder(string name)
    {
        Assert.True(JellyfinNaming.IsExtra(In(HomeVideos, "Video domestici", name, "Beach day.strm")));

        var work = Assert.Single(Plan(Catalog(Folder("f1", name, HomeVideos), HomeVideo("v1", "Beach day", "f1"))).Stubs);

        Assert.Equal(In(HomeVideos, "Video domestici", name + " [f1]", "Beach day.strm"), work.Entry.RelativePath);
        Assert.False(JellyfinNaming.IsExtra(work.Entry.RelativePath));
    }

    [Fact]
    public void AFolderTheServerWouldIgnoreGetsItsId()
    {
        var work = Assert.Single(Plan(Catalog(Folder("f1", "@eaDir", HomeVideos), HomeVideo("v1", "Beach day", "f1")), ignoreRules: new FakeIgnoreRules("@eaDir")).Stubs);

        Assert.Equal(In(HomeVideos, "Video domestici", "@eaDir [f1]", "Beach day.strm"), work.Entry.RelativePath);
    }

    [Fact]
    public void AHomeVideoTheServerWouldIgnoreGetsItsId()
    {
        var work = Assert.Single(Plan(Catalog(HomeVideo("v1", "Clip.minta", HomeVideos)), ignoreRules: new FakeIgnoreRules("Clip.minta.strm")).Stubs);

        Assert.Equal(In(HomeVideos, "Video domestici", $"Clip.minta [{work.Entry.Id[..8]}].strm"), work.Entry.RelativePath);
    }

    [Fact]
    public void AMusicVideoTheServerWouldIgnoreGetsItsIdInItsFolder()
    {
        var work = Assert.Single(Plan(Catalog(MusicVideo("m1", "extrafanart") with { ProductionYear = null }), ignoreRules: new FakeIgnoreRules("extrafanart")).Stubs);

        var folder = $"extrafanart [{work.Entry.Id[..8]}]";
        Assert.Equal(In(MusicVideos, "Video musicali", folder, folder + " - Casa.strm"), work.Entry.RelativePath);
    }

    [Fact]
    public void AHomeVideoAndAFolderLoseTheLeadingDotThatHidesThem()
    {
        var work = Assert.Single(Plan(Catalog(Folder("f1", ".Trip", HomeVideos), HomeVideo("v1", ".Beach", "f1"))).Stubs);

        Assert.Equal(In(HomeVideos, "Video domestici", "Trip", "Beach.strm"), work.Entry.RelativePath);
    }

    [Fact]
    public void AnItemFederatedOnItsServerGetsNoStub()
    {
        var federated = HomeVideo("v1", "Beach day", HomeVideos) with { Versions = [] };

        Assert.Empty(Plan(Catalog(federated, MusicVideo("m1") with { Versions = [] })).Stubs);
    }

    [Fact]
    public void ANewPlanKeepsTheStubsAndTheirPaths()
    {
        var catalog = Catalog(MusicVideo("m1"), HomeVideo("v1", "Beach day", HomeVideos));
        var first = Plan(catalog);

        var second = Plan(catalog, first.State);

        Assert.Equal(
            first.Stubs.Select(work => (work.Entry.Id, work.Entry.RelativePath)).Order(),
            second.Stubs.Select(work => (work.Entry.Id, work.Entry.RelativePath)).Order());
        Assert.All(second.Stubs, work => Assert.False(work.IsNew));
        Assert.Empty(second.Removals);
    }

    [Fact]
    public void ARenamedHomeVideoKeepsItsFile()
    {
        var first = Plan(Catalog(HomeVideo("v1", "Beach day", HomeVideos)));

        var second = Plan(Catalog(HomeVideo("v1", "Sea day", HomeVideos)), first.State);

        Assert.Equal(first.Stubs[0].Entry.RelativePath, Assert.Single(second.Stubs).Entry.RelativePath);
        Assert.Equal("Sea day", XDocument.Parse(second.Stubs[0].Nfo).Root!.Element("title")?.Value);
    }

    [Fact]
    public void AnItemGoneFromItsServerLosesItsStub()
    {
        var first = Plan(Catalog(MusicVideo("m1"), HomeVideo("v1", "Beach day", HomeVideos), HomeVideo("v2", "Other", HomeVideos)));

        var second = Plan(Catalog(MusicVideo("m1"), HomeVideo("v2", "Other", HomeVideos)), first.State);

        var removed = Assert.Single(second.Removals);
        Assert.Equal(StubKind.HomeVideo, removed.Kind);
        Assert.Equal("v1", removed.Sources[0].ItemId);
        Assert.Single(second.State.Stubs, stub => stub.Kind == StubKind.MusicVideo);
    }

    [Fact]
    public void FilmsAndSeriesOfAMixedLibraryGoToItsFilmAndSerieFolders()
    {
        var film = new IndexedItem
        {
            ItemId = "film", Kind = IndexedItemKind.Movie, LibraryId = Mixed, LastSeenUtc = _now, Name = "Film", ProductionYear = 2020,
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1" }, Versions = [Version("film")]
        };
        var series = new IndexedItem { ItemId = "s", Kind = IndexedItemKind.Series, LibraryId = Mixed, LastSeenUtc = _now, Name = "Show", ProductionYear = 2020 };
        var episode = new IndexedItem
        {
            ItemId = "e", Kind = IndexedItemKind.Episode, LibraryId = Mixed, LastSeenUtc = _now, Name = "Episode", SeriesId = "s", SeriesName = "Show",
            ParentIndexNumber = 1, IndexNumber = 1, Versions = [Version("e")]
        };
        ResolvedContent[] contents =
        [
            new(new ContentIdentity("film", "film", ContentKind.Movie, MatchKind.ProviderId), false, null, []) { RemoteItems = [new ResolvedItem(_serverA, "film")] },
            new(new ContentIdentity("show|S1E1", "show", ContentKind.Episode, MatchKind.ProviderId, 1, 1), false, null, []) { RemoteItems = [new ResolvedItem(_serverA, "e")] }
        ];

        var plan = Plan(Catalog(film, series, episode), contents: contents);

        Assert.StartsWith(In(Mixed, "Film") + Path.DirectorySeparatorChar, Assert.Single(plan.Stubs, work => work.Entry.Kind == StubKind.Movie).Entry.RelativePath, StringComparison.Ordinal);
        Assert.StartsWith(In(Mixed, "Serie") + Path.DirectorySeparatorChar, Assert.Single(plan.Stubs, work => work.Entry.Kind == StubKind.Episode).Entry.RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnprotectedMusicVideoIsNotLocked()
    {
        var work = Assert.Single(Plan(Catalog(MusicVideo("m1")), isProtected: false).Stubs);

        Assert.Null(XDocument.Parse(work.Nfo).Root!.Element("lockdata"));
    }
}
