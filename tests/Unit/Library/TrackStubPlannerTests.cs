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

public class TrackStubPlannerTests
{
    private const string Music = TestLibraries.Music;
    private const string Films = TestLibraries.Films;

    private static readonly Guid _server = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly DateTimeOffset _now = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

    private static string MusicFolder => FederatedPaths.TypeFolderOf(_server, Music, TypeFolder.Music);

    private static IndexedItem Track(
        string id,
        string name = "Song",
        int? number = 1,
        int? disc = null,
        string? album = "Album",
        string? albumId = "album1",
        string[]? albumArtists = null,
        string[]? artists = null) => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Audio,
        LibraryId = Music,
        LastSeenUtc = _now,
        Name = name,
        Album = album,
        AlbumId = albumId,
        AlbumPrimaryImageTag = albumId is null ? null : "cover-" + albumId,
        AlbumArtists = albumArtists ?? ["Artist"],
        Artists = artists ?? ["Artist"],
        IndexNumber = number,
        ParentIndexNumber = disc,
        ProductionYear = 2020,
        Genres = ["Rock"],
        RunTimeTicks = 1_800_000_000,
        ProviderIds = new Dictionary<string, string> { ["MusicBrainzTrack"] = "mb-" + id, ["MusicBrainzAlbum"] = "mb-album" },
        Etag = "e1",
        Versions = [new IndexedVersion { MediaSourceId = id, Container = "flac" }]
    };

    private static IndexedItem Film(string id) => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Movie,
        LibraryId = Films,
        LastSeenUtc = _now,
        Name = "Film",
        ProductionYear = 2020,
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1" },
        Versions = [new IndexedVersion { MediaSourceId = id, Height = 1080, Codec = "h264", BitRate = 1000, VideoRange = "SDR" }]
    };

    private static ResolvedContent FilmTitle(string itemId) => new(
        new ContentIdentity("film", "film", ContentKind.Movie, MatchKind.ProviderId),
        false,
        null,
        [])
    {
        RemoteItems = [new ResolvedItem(_server, itemId)]
    };

    private static ResolvedContent TrackTitle(string key, params string[] itemIds) => new(
        new ContentIdentity(key, key, ContentKind.Track, MatchKind.ProviderId),
        false,
        null,
        [])
    {
        RemoteItems = itemIds.Select(itemId => new ResolvedItem(_server, itemId)).ToList()
    };

    private static StubPlan Plan(
        IReadOnlyList<IndexedItem> items,
        StubState? state = null,
        bool isProtected = true,
        IReadOnlyList<ResolvedContent>? contents = null,
        IReadOnlyList<ResolvedContent>? tracks = null,
        bool keepsOlderLayout = false,
        IServerIgnoreRules? ignoreRules = null)
    {
        var catalog = new ServerCatalog { ServerId = _server, State = ServerSyncState.Complete, Items = items };
        var snapshot = new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = IndexHealth.Ok, Servers = [catalog] };
        return StubPlanner.PlanByServer(new StubPlanInput(
            new FederationView(IndexHealth.Ok, 0, contents ?? [], []) { Tracks = tracks ?? [] },
            snapshot,
            state ?? StubState.Empty,
            [new RemoteServerInfo(_server, "Casa", "http://server", RemoteMode.Standard, 0, [Films, Music], null, null)],
            isProtected,
            id => "http://127.0.0.1:8096/JellyMesh/Play/" + id,
            _ => null) { KeepsOlderLayout = keepsOlderLayout, IgnoreRules = ignoreRules ?? new FakeIgnoreRules() });
    }

    private static string ArtistOf(StubPlan plan, string itemId) => Path.GetRelativePath(MusicFolder, WorkOf(plan, itemId).Entry.RelativePath).Split(Path.DirectorySeparatorChar)[0];

    private static StubWork WorkOf(StubPlan plan, string itemId) => Assert.Single(plan.Stubs, work => work.Entry.Sources.Any(source => source.ItemId == itemId));

    private static string PathIn(params string[] parts) => Path.Combine(MusicFolder, Path.Combine(parts));

    [Fact]
    public void ATrackGetsAStubWithNoNfoInItsAlbumFolderUnderTheAlbumArtist()
    {
        var plan = Plan([Track("t1", "Song", 3)]);

        var work = Assert.Single(plan.Stubs);
        Assert.Equal(StubKind.Track, work.Entry.Kind);
        Assert.Equal(PathIn("Artist", "Album", "03 - Song.strm"), work.Entry.RelativePath);
        Assert.Equal(_server, work.Entry.ServerId);
        Assert.Equal("Track|item:" + _server.ToString("N") + ":t1", work.Entry.IdentityKey);
        Assert.Equal([new StubSource(_server, "t1", "t1") { LibraryId = Music }], work.Entry.Sources);
        Assert.True(work.IsNew);
        Assert.Equal(string.Empty, work.NfoPath);
        Assert.Equal(string.Empty, work.Nfo);
        Assert.Equal("http://127.0.0.1:8096/JellyMesh/Play/" + work.Entry.Id, work.StrmUrl);
    }

    [Fact]
    public void ATrackTakesTheIdentityKeyOfTheDedupAndKeepsItsStubWhenTheKeyChanges()
    {
        var first = Plan([Track("t1", "One", 1)]);
        var byTrackId = Plan([Track("t1", "One", 1)], first.State, tracks: [TrackTitle("Track|mbt:mb-t1", "t1")]);
        var byRecording = Plan([Track("t1", "One", 1)], byTrackId.State, tracks: [TrackTitle("Track|mbr:rec", "t1")]);

        var before = Assert.Single(first.Stubs).Entry;
        Assert.Equal("Track|mbt:mb-t1", Assert.Single(byTrackId.Stubs).Entry.IdentityKey);
        var after = Assert.Single(byRecording.Stubs);
        Assert.Equal("Track|mbr:rec", after.Entry.IdentityKey);
        Assert.Equal((before.Id, before.RelativePath), (after.Entry.Id, after.Entry.RelativePath));
        Assert.False(after.IsNew);
        Assert.Empty(byRecording.Removals);
    }

    [Fact]
    public void TwoCopiesOfOneTrackOnAServerKeepTwoStubsAndNoVersionGroup()
    {
        var plan = Plan([Track("t1", "Song", 1), Track("t2", "Song", 1, albumId: "album2")], tracks: [TrackTitle("Track|mbr:rec", "t1", "t2")]);

        Assert.Equal(2, plan.Stubs.Count);
        Assert.All(plan.Stubs, work => Assert.Equal("Track|mbr:rec", work.Entry.IdentityKey));
        Assert.Equal(2, plan.Stubs.Select(work => work.Entry.Id).Distinct().Count());
        Assert.Empty(plan.State.Groups);
    }

    [Fact]
    public void ASecondRunOnTheSameCatalogKeepsEveryStubAndMintsNothing()
    {
        IndexedItem[] items = [Track("t1", "One", 1), Track("t2", "Two", 2), Film("f1")];
        var first = Plan(items, contents: [FilmTitle("f1")]);

        var second = Plan(items, first.State, contents: [FilmTitle("f1")]);

        Assert.Equal(3, first.Stubs.Count);
        Assert.Empty(second.Removals);
        Assert.All(second.Stubs, work => Assert.False(work.IsNew));
        Assert.Equal(
            first.State.Stubs.Select(stub => (stub.Id, stub.RelativePath)).Order(),
            second.State.Stubs.Select(stub => (stub.Id, stub.RelativePath)).Order());
        Assert.Equal(first.Stubs.Select(work => work.ContentHash).Order(), second.Stubs.Select(work => work.ContentHash).Order());
    }

    [Fact]
    public void ATrackGoneFromAServerReadInFullLosesItsStub()
    {
        var first = Plan([Track("t1", "One", 1), Track("t2", "Two", 2)]);

        var second = Plan([Track("t1", "One", 1)], first.State);

        var removed = Assert.Single(second.Removals);
        Assert.Equal(PathIn("Artist", "Album", "02 - Two.strm"), removed.RelativePath);
    }

    [Fact]
    public void TheTracksOfOneRemoteAlbumShareAFolderAndAnotherAlbumOfTheSameNameGetsItsOwn()
    {
        var plan = Plan([Track("t1", "One", 1), Track("t2", "Two", 2), Track("t3", "Three", 1, albumId: "album2")]);

        Assert.Equal(PathIn("Artist", "Album", "01 - One.strm"), WorkOf(plan, "t1").Entry.RelativePath);
        Assert.Equal(PathIn("Artist", "Album", "02 - Two.strm"), WorkOf(plan, "t2").Entry.RelativePath);
        var other = WorkOf(plan, "t3").Entry;
        Assert.Equal(PathIn("Artist", "Album [" + other.Id[..8] + "]", "01 - Three.strm"), other.RelativePath);
    }

    [Fact]
    public void MissingArtistsAndAlbumsTakeTheTrackArtistOrTheUnknownNames()
    {
        var plan = Plan([
            Track("t1", "One", 1, albumArtists: [], artists: ["Singer", "Guest"]),
            Track("t2", "Two", 1, albumId: "album2", albumArtists: [], artists: []),
            Track("t3", "Three", 1, album: null, albumId: null)
        ]);

        Assert.Equal(PathIn("Singer", "Album", "01 - One.strm"), WorkOf(plan, "t1").Entry.RelativePath);
        Assert.Equal(PathIn("Unknown Artist", "Album", "01 - Two.strm"), WorkOf(plan, "t2").Entry.RelativePath);
        Assert.Equal(PathIn("Artist", "Unknown Album", "01 - Three.strm"), WorkOf(plan, "t3").Entry.RelativePath);
    }

    [Fact]
    public void AnAlbumOfMoreThanOneDiscPutsTheDiscBeforeTheNumber()
    {
        var plan = Plan([Track("t1", "One", 1, disc: 1), Track("t2", "Two", 1, disc: 2), Track("t3", "Solo", 4, disc: 1, albumId: "album2")]);

        Assert.Equal(PathIn("Artist", "Album", "1-01 - One.strm"), WorkOf(plan, "t1").Entry.RelativePath);
        Assert.Equal(PathIn("Artist", "Album", "2-01 - Two.strm"), WorkOf(plan, "t2").Entry.RelativePath);
        Assert.EndsWith(Path.DirectorySeparatorChar + "04 - Solo.strm", WorkOf(plan, "t3").Entry.RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public void OneTrackOfEachAlbumCarriesItsCoverAsTheFolderImage()
    {
        var plan = Plan([Track("t2", "Two", 2), Track("t1", "One", 1), Track("t3", "Three", 1, album: null, albumId: null)]);

        var image = Assert.Single(plan.Stubs.SelectMany(work => work.Images));
        Assert.Equal(new StubImage(ImageType.Primary, PathIn("Artist", "Album", "folder"), _server, "album1", null), image);
        Assert.Single(WorkOf(plan, "t1").Images);
        Assert.Equal("Primary=cover-album1;", WorkOf(plan, "t1").ImageTag);
        Assert.Equal(string.Empty, WorkOf(plan, "t2").ImageTag);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheTrackFieldsAreWrittenWithOrWithoutProtection(bool isProtected)
    {
        var plan = Plan([Track("t1", "Song", 3, disc: 2, artists: ["Artist", "Guest"])], isProtected: isProtected);

        var metadata = Assert.Single(plan.Stubs).Metadata;
        Assert.Equal(isProtected, metadata.IsProtected);
        Assert.Equal("Song", metadata.Title);
        Assert.Equal(2020, metadata.Year);
        Assert.Equal("Album", metadata.Album);
        Assert.Equal(["Artist", "Guest"], metadata.Artists);
        Assert.Equal(["Artist"], metadata.AlbumArtists);
        Assert.Equal(3, metadata.TrackNumber);
        Assert.Equal(2, metadata.DiscNumber);
        Assert.Equal(["Rock"], metadata.Genres);
        Assert.Equal("mb-t1", metadata.ProviderIds["MusicBrainzTrack"]);
    }

    [Fact]
    public void TheContentHashFollowsTheTrackMetadata()
    {
        var first = Plan([Track("t1", "Song", 1)]);

        var same = Plan([Track("t1", "Song", 1)], first.State);
        var renamedAlbum = Plan([Track("t1", "Song", 1) with { Album = "Album (Remastered)" }], first.State);
        var newArtist = Plan([Track("t1", "Song", 1, artists: ["Artist", "Guest"])], first.State);

        var hash = Assert.Single(first.Stubs).ContentHash;
        Assert.Equal(hash, Assert.Single(same.Stubs).ContentHash);
        Assert.NotEqual(hash, Assert.Single(renamedAlbum.Stubs).ContentHash);
        Assert.NotEqual(hash, Assert.Single(newArtist.Stubs).ContentHash);
        Assert.Equal(Assert.Single(first.Stubs).Entry.RelativePath, Assert.Single(renamedAlbum.Stubs).Entry.RelativePath);
    }

    [Fact]
    public void ANewTrackOfARenamedAlbumGoesIntoTheFolderTheAlbumAlreadyUses()
    {
        var first = Plan([Track("t1", "One", 1)]);

        var second = Plan([Track("t1", "One", 1, album: "Album (Remastered)", albumArtists: ["Band"]), Track("t2", "Two", 2, album: "Album (Remastered)", albumArtists: ["Band"])], first.State);

        Assert.Equal(PathIn("Artist", "Album", "01 - One.strm"), WorkOf(second, "t1").Entry.RelativePath);
        Assert.Equal(PathIn("Artist", "Album", "02 - Two.strm"), WorkOf(second, "t2").Entry.RelativePath);
    }

    [Fact]
    public void ASecondDiscNamesOnlyTheNewTracksByDiscInTheSameFolder()
    {
        var first = Plan([Track("t1", "One", 1, disc: 1)]);

        var second = Plan([Track("t1", "One", 1, disc: 1), Track("t2", "Two", 1, disc: 2)], first.State);

        Assert.Equal(PathIn("Artist", "Album", "01 - One.strm"), WorkOf(second, "t1").Entry.RelativePath);
        Assert.Equal(PathIn("Artist", "Album", "2-01 - Two.strm"), WorkOf(second, "t2").Entry.RelativePath);
        Assert.Empty(second.Removals);
    }

    [Fact]
    public void HostileNamesStayInsideTheMusicFolderOfTheServer()
    {
        var plan = Plan([
            Track("t1", "../../x", 1, album: "../..", albumArtists: [".."]),
            Track("t2", "a/b\\c", 2, album: "///", albumId: "album2", albumArtists: ["/"]),
            Track("t3", "...", 3, album: "  ", albumId: "album3", albumArtists: ["\0"], artists: [])
        ]);

        Assert.Equal(3, plan.Stubs.Count);
        Assert.All(plan.Stubs, work =>
        {
            var parts = Path.GetRelativePath(MusicFolder, work.Entry.RelativePath).Split(Path.DirectorySeparatorChar);
            Assert.Equal(3, parts.Length);
            Assert.All(parts, part => Assert.False(string.IsNullOrWhiteSpace(part.Trim('.'))));
            Assert.StartsWith(Path.Combine("/root", MusicFolder) + Path.DirectorySeparatorChar, FederatedPaths.Resolve("/root", work.Entry.RelativePath), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ATrackWithNoVersionOfItsOwnIsNotImported()
    {
        var plan = Plan([Track("t1") with { Versions = [] }]);

        Assert.Empty(plan.Stubs);
    }

    [Fact]
    public void ATrackGoesToTheRemoteLibraryLayoutBesideAStubOfAnOlderLayout()
    {
        // §10 row 444: new titles go to the remote library layout while the migration empties the older one (Q49).
        var flat = new StubEntry { Id = "old", Kind = StubKind.Movie, RelativePath = FederatedPaths.MovieStub("Film (2020)"), IdentityKey = "film", Sources = [] };

        var plan = Plan([Track("t1")], new StubState { Stubs = [flat] });

        Assert.StartsWith(MusicFolder + Path.DirectorySeparatorChar, WorkOf(plan, "t1").Entry.RelativePath, StringComparison.Ordinal);
        Assert.Contains(flat, plan.State.Stubs);
    }

    [Fact]
    public void AFolderThatKeepsAnOlderLayoutGetsNoTracks()
    {
        var plan = Plan([Track("t1")], keepsOlderLayout: true);

        Assert.DoesNotContain(plan.Stubs, work => work.Entry.Kind == StubKind.Track);
    }

    // Row 568: an artist folder the server would skip gets an id, and every album of that artist goes into it.
    [Fact]
    public void AnArtistFolderTheServerWouldSkipGetsItsIdAndKeepsEveryAlbumOfTheArtist()
    {
        var rules = new FakeIgnoreRules((_, name, isDirectory) => isDirectory && name == "@eaDir");
        IndexedItem[] items = [Track("t1", "One", 1, albumArtists: ["@eaDir"]), Track("t2", "Two", 1, album: "Other", albumId: "album2", albumArtists: ["@eaDir"])];

        var plan = Plan(items, ignoreRules: rules);

        var folder = ArtistOf(plan, "t1");
        Assert.Matches("^@eaDir \\[[0-9a-f]{8}\\]$", folder);
        Assert.Contains(plan.Stubs, work => folder == "@eaDir [" + work.Entry.Id[..8] + "]");
        Assert.Equal(folder, ArtistOf(plan, "t2"));
        Assert.Equal(PathIn(folder, "Album", "01 - One.strm"), WorkOf(plan, "t1").Entry.RelativePath);
        Assert.Equal(PathIn(folder, "Other", "01 - Two.strm"), WorkOf(plan, "t2").Entry.RelativePath);
    }

    [Fact]
    public void ANewAlbumOfAnArtistGoesIntoTheFolderWithTheIdTheArtistAlreadyHas()
    {
        var rules = new FakeIgnoreRules((_, name, isDirectory) => isDirectory && name == "@eaDir");
        var first = Plan([Track("t1", "One", 1, albumArtists: ["@eaDir"])], ignoreRules: rules);

        var second = Plan([Track("t1", "One", 1, albumArtists: ["@eaDir"]), Track("t2", "Two", 1, album: "Other", albumId: "album2", albumArtists: ["@eaDir"])], first.State, ignoreRules: rules);

        var folder = ArtistOf(first, "t1");
        Assert.StartsWith("@eaDir [", folder, StringComparison.Ordinal);
        Assert.Equal(WorkOf(first, "t1").Entry.RelativePath, WorkOf(second, "t1").Entry.RelativePath);
        Assert.Equal(PathIn(folder, "Other", "01 - Two.strm"), WorkOf(second, "t2").Entry.RelativePath);
    }

    // Existing stubs keep their paths: a rule that comes later changes only the folders made after it.
    [Fact]
    public void ATrackThatExistsKeepsItsPathAndItsArtistFolderWhenTheServerStartsSkippingIt()
    {
        var first = Plan([Track("t1", "One", 1)]);
        var rules = new FakeIgnoreRules("Artist", "Album", "01 - One.strm", "01 - Two.strm");

        var second = Plan([Track("t1", "One", 1), Track("t2", "Two", 1, album: "Other", albumId: "album2")], first.State, ignoreRules: rules);

        Assert.Equal(PathIn("Artist", "Album", "01 - One.strm"), WorkOf(second, "t1").Entry.RelativePath);
        var other = WorkOf(second, "t2").Entry;
        Assert.Equal(PathIn("Artist", "Other", "01 - Two [" + other.Id[..8] + "].strm"), other.RelativePath);
        Assert.Empty(second.Removals);
    }

    // A folder name that only looks like "name [id8]" is the whole name of its artist.
    [Fact]
    public void AnArtistFolderWhoseNameEndsInEightOtherCharactersInBracketsStaysTheFolderOfItsOwnArtist()
    {
        var first = Plan([Track("t1", "One", 1, albumArtists: ["Band [Explicit]"])]);

        var second = Plan([Track("t1", "One", 1, albumArtists: ["Band [Explicit]"]), Track("t2", "Two", 1, album: "Other", albumId: "album2", albumArtists: ["Band"])], first.State);

        Assert.Equal("Band [Explicit]", ArtistOf(second, "t1"));
        Assert.Equal(PathIn("Band", "Other", "01 - Two.strm"), WorkOf(second, "t2").Entry.RelativePath);
    }

    [Fact]
    public void AnAlbumFolderTheServerWouldSkipGetsItsId()
    {
        var work = Assert.Single(Plan([Track("t1", "Song", 1, album: "extrafanart")], ignoreRules: new FakeIgnoreRules((_, name, isDirectory) => isDirectory && name == "extrafanart")).Stubs);

        Assert.Equal(PathIn("Artist", "extrafanart [" + work.Entry.Id[..8] + "]", "01 - Song.strm"), work.Entry.RelativePath);
    }

    [Fact]
    public void AnAlbumFolderWhoseFilesTheServerWouldSkipGetsItsId()
    {
        var rules = new FakeIgnoreRules((folder, _, isDirectory) => !isDirectory && Path.GetFileName(folder) == "sample");

        var work = Assert.Single(Plan([Track("t1", "Song", 1, album: "sample")], ignoreRules: rules).Stubs);

        Assert.Equal(PathIn("Artist", "sample [" + work.Entry.Id[..8] + "]", "01 - Song.strm"), work.Entry.RelativePath);
    }

    [Fact]
    public void ATrackFileTheServerWouldSkipGetsItsId()
    {
        var rules = new FakeIgnoreRules((_, name, isDirectory) => !isDirectory && name == "01 - Song.strm");

        var plan = Plan([Track("t1", "Song", 1), Track("t2", "Other", 2)], ignoreRules: rules);

        var work = WorkOf(plan, "t1").Entry;
        Assert.Equal(PathIn("Artist", "Album", "01 - Song [" + work.Id[..8] + "].strm"), work.RelativePath);
        Assert.Equal(PathIn("Artist", "Album", "02 - Other.strm"), WorkOf(plan, "t2").Entry.RelativePath);
    }

    // Row 555: the names taken from a title are cut at 120 UTF-8 bytes, so at 40 characters of three bytes.
    [Fact]
    public void TheNamesOfATrackAreCutAt120Bytes()
    {
        var work = Assert.Single(Plan([Track("t1", new string('漢', 100), 3, album: new string('曲', 100), albumArtists: [new string('歌', 100)])]).Stubs);

        Assert.Equal(PathIn(new string('歌', 40), new string('曲', 40), "03 - " + new string('漢', 40) + ".strm"), work.Entry.RelativePath);
        Assert.True(FederatedPaths.FitsInNames(work.Entry.RelativePath));
    }
}
