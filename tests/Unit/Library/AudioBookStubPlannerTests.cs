using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// Audiobooks in Libri/<author>/<book>/<NN - part>.strm, planned like tracks and with no dedup (rows 454 and 510).
public class AudioBookStubPlannerTests
{
    private const string Books = TestLibraries.Books;

    private static readonly Guid _server = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly DateTimeOffset _now = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    private static string BooksFolder => FederatedPaths.TypeFolderOf(_server, Books, TypeFolder.Books);

    // A part as Jellyfin gives it: the book is the album, the narrator the artist, and it has no album item.
    private static IndexedItem Part(string id, string name = "Part", int? number = 1, string? book = "Story", string? author = "Narrator") => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.AudioBook,
        LibraryId = Books,
        LastSeenUtc = _now,
        Name = name,
        Album = book,
        AlbumArtists = author is null ? [] : [author],
        Artists = author is null ? [] : [author],
        IndexNumber = number,
        ProductionYear = 2002,
        Genres = ["Fantasy"],
        Overview = "Once upon a time",
        ProviderIds = new Dictionary<string, string> { ["Audible"] = "B00" },
        Versions = [new IndexedVersion { MediaSourceId = id, Container = "mp3" }]
    };

    private static StubPlan Plan(IReadOnlyList<IndexedItem> items, StubState? state = null, bool isProtected = true, bool keepsOlderLayout = false)
    {
        var catalog = new ServerCatalog { ServerId = _server, State = ServerSyncState.Complete, Items = items };
        var snapshot = new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = IndexHealth.Ok, Servers = [catalog] };
        return StubPlanner.PlanByServer(new StubPlanInput(
            new FederationView(IndexHealth.Ok, 0, [], []),
            snapshot,
            state ?? StubState.Empty,
            [new RemoteServerInfo(_server, "Casa", "http://server", RemoteMode.Standard, 0, [Books], null, null)],
            isProtected,
            id => "http://127.0.0.1:8096/JellyMesh/Play/" + id,
            _ => null) { KeepsOlderLayout = keepsOlderLayout });
    }

    private static StubWork WorkOf(StubPlan plan, string itemId) => Assert.Single(plan.Stubs, work => work.Entry.Sources.Any(source => source.ItemId == itemId));

    private static string PathIn(params string[] parts) => Path.Combine(BooksFolder, Path.Combine(parts));

    [Fact]
    public void APartGetsAStubWithNoNfoInTheFolderOfItsBookUnderTheAuthor()
    {
        var work = Assert.Single(Plan([Part("p1", "The start", 3)]).Stubs);

        Assert.Equal(StubKind.AudioBook, work.Entry.Kind);
        Assert.Equal(PathIn("Narrator", "Story", "03 - The start.strm"), work.Entry.RelativePath);
        Assert.Equal(_server, work.Entry.ServerId);
        Assert.Equal("AudioBook|item:" + _server.ToString("N") + ":p1", work.Entry.IdentityKey);
        Assert.Equal([new StubSource(_server, "p1", "p1") { LibraryId = Books }], work.Entry.Sources);
        Assert.True(work.IsNew);
        Assert.Equal((string.Empty, string.Empty), (work.NfoPath, work.Nfo));
        Assert.Equal("http://127.0.0.1:8096/JellyMesh/Play/" + work.Entry.Id, work.StrmUrl);
    }

    [Fact]
    public void ASingleFileAudiobookGetsTheFolderOfItsBookToo()
    {
        var work = Assert.Single(Plan([Part("p1", "Story", null)]).Stubs);

        Assert.Equal(PathIn("Narrator", "Story", "Story.strm"), work.Entry.RelativePath);
    }

    [Fact]
    public void ThePartsOfOneBookShareAFolderAndAMissingAuthorOrBookTakesTheNamesOfMusic()
    {
        var plan = Plan([Part("p1", "One", 1), Part("p2", "Two", 2), Part("p3", "Lost", 1, book: null, author: null)]);

        Assert.Equal(PathIn("Narrator", "Story", "01 - One.strm"), WorkOf(plan, "p1").Entry.RelativePath);
        Assert.Equal(PathIn("Narrator", "Story", "02 - Two.strm"), WorkOf(plan, "p2").Entry.RelativePath);
        Assert.Equal(PathIn("Unknown Artist", "Unknown Album", "01 - Lost.strm"), WorkOf(plan, "p3").Entry.RelativePath);
    }

    // Row 454: no dedup for audiobooks, so two copies stay two titles with no version group.
    [Fact]
    public void TwoCopiesOfAPartAreTwoStubsAndNoVersionGroup()
    {
        var plan = Plan([Part("p1", "One", 1), Part("p2", "One", 1)]);

        Assert.Equal(2, plan.Stubs.Select(work => work.Entry.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(2, plan.Stubs.Select(work => work.Entry.IdentityKey).Distinct().Count());
        Assert.Empty(plan.State.Groups);
    }

    [Fact]
    public void ASecondRunKeepsEveryStubAndAPartThatIsGoneLosesIts()
    {
        IndexedItem[] items = [Part("p1", "One", 1), Part("p2", "Two", 2)];
        var first = Plan(items);

        var second = Plan(items, first.State);
        var third = Plan([items[0] with { Name = "Renamed", Album = "Another" }], second.State);

        Assert.Empty(second.Removals);
        Assert.All(second.Stubs, work => Assert.False(work.IsNew));
        Assert.Equal(first.Stubs.Select(work => (work.Entry.RelativePath, work.ContentHash)).Order(), second.Stubs.Select(work => (work.Entry.RelativePath, work.ContentHash)).Order());
        Assert.Equal(WorkOf(first, "p2").Entry.RelativePath, Assert.Single(third.Removals).RelativePath);
        Assert.Equal(WorkOf(first, "p1").Entry.RelativePath, WorkOf(third, "p1").Entry.RelativePath);
        Assert.NotEqual(WorkOf(first, "p1").ContentHash, WorkOf(third, "p1").ContentHash);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThePartFieldsAreThoseOfATrackAndThePlotWithOrWithoutProtection(bool isProtected)
    {
        var metadata = Assert.Single(Plan([Part("p1", "One", 3)], isProtected: isProtected).Stubs).Metadata;

        Assert.Equal(isProtected, metadata.IsProtected);
        Assert.Equal(("One", "Story", 3, 2002, "Once upon a time"), (metadata.Title, metadata.Album, metadata.TrackNumber, metadata.Year, metadata.Overview));
        Assert.Equal(["Narrator"], metadata.Artists);
        Assert.Equal(["Narrator"], metadata.AlbumArtists);
        Assert.Equal(["Fantasy"], metadata.Genres);
        Assert.Equal("B00", metadata.ProviderIds["Audible"]);
    }

    [Fact]
    public void APlotChangeReachesTheContentHash()
    {
        var first = Plan([Part("p1")]);

        var second = Plan([Part("p1") with { Overview = "Another plot" }], first.State);

        Assert.NotEqual(Assert.Single(first.Stubs).ContentHash, Assert.Single(second.Stubs).ContentHash);
    }

    [Fact]
    public void APartWithNoVersionOfItsOwnIsNotImported()
    {
        Assert.Empty(Plan([Part("p1") with { Versions = [] }]).Stubs);
    }

    [Fact]
    public void AFolderThatKeepsAnOlderLayoutGetsNoNewAudiobooksAndKeepsTheOnesItHas()
    {
        var first = Plan([Part("p1", "One", 1)]);

        var legacy = Plan([Part("p2", "Two", 2)], first.State, keepsOlderLayout: true);

        Assert.Empty(legacy.Stubs);
        Assert.Empty(legacy.Removals);
        Assert.Equal(first.State.Stubs.Select(stub => stub.Id), legacy.State.Stubs.Select(stub => stub.Id));
    }

    private static IndexedItem Book(string id, string name) => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Book,
        LibraryId = Books,
        LastSeenUtc = _now,
        Name = name,
        Extension = "epub"
    };

    private static string FolderOf(StubPlan plan, string itemId) => Path.GetRelativePath(BooksFolder, WorkOf(plan, itemId).Entry.RelativePath).Split(Path.DirectorySeparatorChar)[0];

    // The folder of a book goes whole with it, so it is never the author folder of an audiobook, whichever comes first.
    [Theory]
    [InlineData("Narrator")]
    [InlineData("NARRATOR")]
    public void ABookAndAnAuthorNamedAlikeNeverShareAFolder(string title)
    {
        var together = Plan([Part("p1"), Book("b1", title)]);
        var bookFirst = Plan([Part("p1"), Book("b1", title)], Plan([Book("b1", title)]).State);
        var partFirst = Plan([Part("p1"), Book("b1", title)], Plan([Part("p1")]).State);

        Assert.All([together, bookFirst, partFirst], plan =>
        {
            Assert.Equal(2, plan.Stubs.Count);
            Assert.NotEqual(FolderOf(plan, "b1"), FolderOf(plan, "p1"), StringComparer.OrdinalIgnoreCase);
        });
        Assert.Equal(title, FolderOf(bookFirst, "b1"));
        Assert.Equal("Narrator", FolderOf(partFirst, "p1"));
    }

    // A book that leaves in the same run is still on disk, so its name is not free yet.
    [Fact]
    public void AnAuthorNeverTakesTheFolderOfABookThatIsLeaving()
    {
        var plan = Plan([Part("p1")], Plan([Book("b1", "Narrator")]).State);

        Assert.Equal("Narrator", Path.GetFileName(Path.GetDirectoryName(Assert.Single(plan.Removals).RelativePath)));
        Assert.NotEqual("Narrator", FolderOf(plan, "p1"), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheAudiobooksOfAnAuthorShareOneFolderAlsoWhenItsNameIsThatOfABook()
    {
        IndexedItem[] first = [Book("b1", "Narrator"), Part("p1", "One", 1, "Story"), Part("p2", "One", 1, "Tale")];
        var state = Plan(first).State;

        var second = Plan([.. first, Part("p3", "One", 1, "Saga"), Part("p4", "One", 1, "Epic", "narrator")], state);
        var withoutBook = Plan([.. first.Skip(1), Part("p3", "One", 1, "Saga")], state);

        var folder = FolderOf(second, "p1");
        Assert.StartsWith("Narrator [", folder, StringComparison.Ordinal);
        Assert.All(["p2", "p3", "p4"], id => Assert.Equal(folder, FolderOf(second, id)));
        Assert.Equal(folder, FolderOf(withoutBook, "p3"));
    }

    [Fact]
    public void HostileNamesStayInsideTheBooksFolderOfTheServer()
    {
        var plan = Plan([Part("p1", "../../x", 1, "../..", ".."), Part("p2", "a/b\\c", 2, "///", "/"), Part("p3", "...", 3, "  ", "\0")]);

        Assert.Equal(3, plan.Stubs.Count);
        Assert.All(plan.Stubs, work =>
        {
            Assert.Equal(3, Path.GetRelativePath(BooksFolder, work.Entry.RelativePath).Split(Path.DirectorySeparatorChar).Length);
            Assert.StartsWith(Path.Combine("/root", BooksFolder) + Path.DirectorySeparatorChar, FederatedPaths.Resolve("/root", work.Entry.RelativePath), StringComparison.Ordinal);
        });
    }
}
