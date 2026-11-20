using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class BookStubPlannerTests
{
    private const string Books = TestLibraries.Books;

    private static readonly Guid _serverA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly DateTimeOffset _now = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

    private static string Folder => FederatedPaths.TypeFolderOf(_serverA, Books, TypeFolder.Books);

    private static IndexedItem Book(string id, string name = "Dune", string? extension = "epub") => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Book,
        LibraryId = Books,
        LastSeenUtc = _now,
        Name = name,
        ProductionYear = 1965,
        Overview = "Spice",
        ProviderIds = new Dictionary<string, string> { ["Isbn"] = "9780441013593" },
        ImageTags = new Dictionary<string, string> { ["Primary"] = "cover1" },
        Extension = extension
    };

    private static StubPlan Plan(StubState? state = null, ServerSyncState sync = ServerSyncState.Complete, params IndexedItem[] items)
    {
        var snapshot = new IndexSnapshot
        {
            Version = IndexSnapshot.CurrentVersion,
            Health = IndexHealth.Ok,
            Servers = [new ServerCatalog { ServerId = _serverA, State = sync, Items = items }]
        };
        return StubPlanner.PlanByServer(new StubPlanInput(
            new FederationView(IndexHealth.Ok, 0, [], []),
            snapshot,
            state ?? StubState.Empty,
            [new RemoteServerInfo(_serverA, "Casa", "http://server", RemoteMode.Standard, 0, [Books], null, null)],
            true,
            id => "http://127.0.0.1:8096/JellyMesh/Play/" + id,
            _ => null));
    }

    private static StubEntry Existing(string id, string itemId, string relative) => new()
    {
        Id = id,
        ServerId = _serverA,
        Kind = StubKind.Book,
        RelativePath = relative,
        Sources = [new StubSource(_serverA, itemId, itemId) { LibraryId = Books }],
        IdentityKey = string.Empty
    };

    [Fact]
    public void ABookGetsAPlaceholderInItsOwnFolderWithItsCover()
    {
        var plan = Plan(items: Book("b1"));

        var work = Assert.Single(plan.Stubs);
        Assert.Equal(StubKind.Book, work.Entry.Kind);
        Assert.Equal(Path.Combine(Folder, "Dune (1965)", "Dune (1965).epub"), work.Entry.RelativePath);
        Assert.Equal(_serverA, work.Entry.ServerId);
        var source = Assert.Single(work.Entry.Sources);
        Assert.Equal((_serverA, "b1", "b1", Books), (source.ServerId, source.ItemId, source.MediaSourceId, source.LibraryId));
        var image = Assert.Single(work.Images);
        Assert.Equal((ImageType.Primary, Path.Combine(Folder, "Dune (1965)", "Dune (1965)")), (image.Type, image.RelativeStem));
        Assert.Equal("Dune", work.Metadata.Title);
        Assert.True(work.Metadata.IsProtected);
        Assert.Empty(work.Nfo);
        Assert.Empty(work.StrmUrl);
        Assert.Contains(plan.State.Stubs, stub => stub.Id == work.Entry.Id);
    }

    [Fact]
    public void TwoBooksWithOneTitleGetTwoFolders()
    {
        var plan = Plan(items: [Book("b1"), Book("b2")]);

        Assert.Equal(2, plan.Stubs.Select(work => Path.GetDirectoryName(work.Entry.RelativePath)).Distinct().Count());
    }

    [Fact]
    public void AnExistingBookStubKeepsItsIdAndPath()
    {
        var relative = Path.Combine(Folder, "Old name", "Old name.epub");
        var plan = Plan(StubState.Empty with { Stubs = [Existing("s1", "b1", relative)] }, items: Book("b1"));

        var work = Assert.Single(plan.Stubs);
        Assert.Equal(("s1", relative), (work.Entry.Id, work.Entry.RelativePath));
        Assert.False(work.IsNew);
        Assert.Empty(plan.Removals);
    }

    [Fact]
    public void ABookGoneFromACompleteReadIsRemoved()
    {
        var stub = Existing("s1", "b1", Path.Combine(Folder, "Dune", "Dune.epub"));
        var plan = Plan(StubState.Empty with { Stubs = [stub] }, ServerSyncState.Complete, Book("other", "Other"));

        Assert.Equal("s1", Assert.Single(plan.Removals).Id);
        Assert.DoesNotContain(plan.Stubs, work => work.Entry.Id == "s1");
    }

    [Fact]
    public void ABookWithAnotherFileNowIsANewStub()
    {
        var stub = Existing("s1", "b1", Path.Combine(Folder, "Dune", "Dune.pdf"));
        var plan = Plan(StubState.Empty with { Stubs = [stub] }, items: Book("b1"));

        Assert.EndsWith(".epub", Assert.Single(plan.Stubs).Entry.RelativePath, StringComparison.Ordinal);
        Assert.Equal("s1", Assert.Single(plan.Removals).Id);
    }

    [Fact]
    public void ABookWithNoKnownFormatIsLeftOut()
    {
        Assert.Empty(Plan(items: Book("b1", extension: null)).Stubs);
    }

    [Fact]
    public void TheMetadataCarriesTheMarkOfThePlaceholderOverOneTheIndexHad()
    {
        var state = StubState.Empty with { Stubs = [Existing("s1", "b1", Path.Combine(Folder, "Dune (1965)", "Dune (1965).epub"))] };
        var book = Book("b1") with { ProviderIds = new Dictionary<string, string> { ["Isbn"] = "9780441013593", ["jellymesh"] = "other" } };

        var ids = Assert.Single(Plan(state, items: book).Stubs).Metadata.ProviderIds;

        Assert.Equal(new Dictionary<string, string> { ["Isbn"] = "9780441013593", [FederatedPlaceholderMark.ProviderId] = "s1" }, ids);
    }

    [Fact]
    public void TheHashFollowsTheMetadata()
    {
        var state = StubState.Empty with { Stubs = [Existing("s1", "b1", Path.Combine(Folder, "Dune (1965)", "Dune (1965).epub"))] };
        var first = Assert.Single(Plan(state, items: Book("b1")).Stubs).ContentHash;
        var same = Assert.Single(Plan(state, items: Book("b1")).Stubs).ContentHash;
        var changed = Assert.Single(Plan(state, items: Book("b1") with { Overview = "Sand" }).Stubs).ContentHash;

        Assert.Equal(first, same);
        Assert.NotEqual(first, changed);
    }
}
