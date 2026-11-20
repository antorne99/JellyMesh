using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class BookMaterializerTests : IDisposable
{
    private readonly StubFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static RemoteItem Dune(string overview = "Spice") => new()
    {
        Id = "b1",
        Name = "Dune",
        ProductionYear = 1965,
        Overview = overview,
        Path = "/books/Dune/Dune.epub",
        ImageTags = new Dictionary<string, string> { ["Primary"] = "c1" }
    };

    private async Task<RemoteServerInfo> AddBookServerAsync(RemoteItem book)
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.Books, "Libri", "books"));
        _fixture.Sync.Client.Add(TestLibraries.Books, RemoteItemType.Book, book);
        return await _fixture.Sync.AddServerAsync("A", TestLibraries.Books);
    }

    private string BookPath(RemoteServerInfo server, string file = "Dune (1965).epub")
        => _fixture.PathIn(server, TestLibraries.Books, "Libri", "Dune (1965)", file);

    [Fact]
    public async Task ABookBecomesAPlaceholderAndACoverWithNoStrmNfoOrOpf()
    {
        var a = await AddBookServerAsync(Dune());

        await _fixture.SyncAndMaterializeAsync();

        var folder = Path.GetDirectoryName(BookPath(a))!;
        Assert.Equal(["Dune (1965).epub", "Dune (1965).jpg"], Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(BookPlaceholders.Epub(), await File.ReadAllBytesAsync(BookPath(a)));
        var stub = Assert.Single(_fixture.Registry.State.Stubs);
        Assert.Equal(StubKind.Book, stub.Kind);
        _fixture.Library.Verify(library => library.QueueLibraryScan(), Times.Once);
    }

    [Fact]
    public async Task AnIndexedBookGetsTheRemoteMetadataLockedAtTheNextRun()
    {
        var a = await AddBookServerAsync(Dune());
        await _fixture.SyncAndMaterializeAsync();
        var item = new Book { Id = Guid.NewGuid(), Path = BookPath(a), Name = "Dune (1965)" };
        _fixture.Library.Setup(library => library.FindByPath(BookPath(a), false)).Returns(item);

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(("Dune", 1965, "Spice", true), (item.Name, item.ProductionYear, item.Overview, item.IsLocked));
        Assert.Equal(BookPlaceholders.Epub(), await File.ReadAllBytesAsync(BookPath(a)));
    }

    // The shape another JellyMesh skips (D62) is the one this server writes, so a change of layout cannot open a cycle.
    [Fact]
    public async Task APlaceholderWrittenHereHasTheShapeAnotherJellyMeshSkips()
    {
        var a = await AddBookServerAsync(Dune());

        await _fixture.SyncAndMaterializeAsync();

        Assert.True(File.Exists(BookPath(a)));
        Assert.True(FederatedBookMark.IsPlaceholder(BookPath(a), null));
        Assert.True(FederatedBookMark.IsPlaceholder(Path.Combine(_fixture.Root, Assert.Single(_fixture.Registry.State.Stubs).RelativePath), null));
    }

    [Fact]
    public async Task ABookItemCarriesTheMarkOfAPlaceholderWithTheRemoteIds()
    {
        var a = await AddBookServerAsync(Dune() with { ProviderIds = new Dictionary<string, string?> { ["Isbn"] = "9780441013593" } });
        await _fixture.SyncAndMaterializeAsync();
        var item = new Book { Id = Guid.NewGuid(), Path = BookPath(a), Name = "Dune (1965)" };
        _fixture.Library.Setup(library => library.FindByPath(BookPath(a), false)).Returns(item);

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(Assert.Single(_fixture.Registry.State.Stubs).Id, item.GetProviderId(FederatedBookMark.ProviderId));
        Assert.Equal("9780441013593", item.GetProviderId("Isbn"));
    }

    // D62: without protection the plugin still writes all the book data, only with no lock, as for music.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABookGetsAllItsDataLockedOnlyWithProtection(bool isProtected)
    {
        _fixture.Sync.Store.Configuration.MetadataProtection = isProtected;
        var a = await AddBookServerAsync(Dune() with
        {
            Genres = ["Fantascienza"],
            Studios = [new RemoteNamedItem("Chilton")],
            People = [new RemotePerson("Frank Herbert", null, "Author")],
            SeriesName = "Le cronache di Dune",
            ProviderIds = new Dictionary<string, string?> { ["Isbn"] = "9780441013593" }
        });
        await _fixture.SyncAndMaterializeAsync();
        var item = new Book { Id = Guid.NewGuid(), Path = BookPath(a), Name = "Dune (1965)" };
        _fixture.Library.Setup(library => library.FindByPath(BookPath(a), false)).Returns(item);

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(isProtected, item.IsLocked);
        Assert.Empty(item.LockedFields);
        Assert.Equal(("Dune", 1965, "Spice", "Le cronache di Dune"), (item.Name, item.ProductionYear, item.Overview, item.SeriesName));
        Assert.Equal(["Fantascienza"], item.Genres);
        Assert.Equal(["Chilton"], item.Studios);
        Assert.Equal("9780441013593", item.GetProviderId("Isbn"));
        var people = Assert.Single(_fixture.Library.Invocations, call => call.Method.Name == nameof(ILibraryManager.UpdatePeopleAsync) && call.Arguments[0] == item);
        var author = Assert.Single((IEnumerable<PersonInfo>)people.Arguments[1]);
        Assert.Equal(("Frank Herbert", PersonKind.Author), (author.Name, author.Type));
    }

    [Fact]
    public async Task AChangedBookReachesItsItem()
    {
        var a = await AddBookServerAsync(Dune());
        await _fixture.SyncAndMaterializeAsync();
        var item = new Book { Id = Guid.NewGuid(), Path = BookPath(a), Name = "Dune" };
        _fixture.Library.Setup(library => library.FindByPath(BookPath(a), false)).Returns(item);
        await _fixture.SyncAndMaterializeAsync();
        _fixture.Sync.Client.Items[(TestLibraries.Books, RemoteItemType.Book)][0] = Dune("Sand");

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal("Sand", item.Overview);
    }

    // No .nfo brings the metadata back to an item Jellyfin made again at the same path, so the nightly pass does.
    [Fact]
    public async Task AnItemMadeAgainGetsItsMetadataBackAtTheNightlyPass()
    {
        var a = await AddBookServerAsync(Dune());
        await _fixture.SyncAndMaterializeAsync();
        _fixture.Library.Setup(library => library.FindByPath(BookPath(a), false)).Returns(new Book { Id = Guid.NewGuid(), Path = BookPath(a) });
        await _fixture.SyncAndMaterializeAsync();
        var again = new Book { Id = Guid.NewGuid(), Path = BookPath(a), Name = "Dune (1965)" };
        _fixture.Library.Setup(library => library.FindByPath(BookPath(a), false)).Returns(again);

        await _fixture.Materializer.MaterializeAsync(false, CancellationToken.None);
        Assert.False(again.IsLocked);
        await _fixture.Materializer.MaterializeAsync(true, CancellationToken.None);

        Assert.Equal(("Dune", true), (again.Name, again.IsLocked));
    }

    // No cycles (D62): a remote read in standard mode sends the placeholders of its own JellyMesh like any book.
    [Fact]
    public async Task APlaceholderOfAnotherJellyMeshReadInStandardModeGetsNoStub()
    {
        var shaped = "/srv/f/" + Guid.NewGuid().ToString("D") + "/" + Guid.NewGuid().ToString("N") + "/Libri/Dune (1965)/Dune (1965).epub";
        var a = await AddBookServerAsync(Dune() with { Path = shaped });
        _fixture.Sync.Client.Add(TestLibraries.Books, RemoteItemType.Book, Dune() with { Id = "b2", Name = "Marked", ProviderIds = new Dictionary<string, string?> { ["JellyMesh"] = "s1" } });

        await _fixture.SyncAndMaterializeAsync();

        Assert.NotEqual(RemoteMode.JellyMesh, a.Mode);
        Assert.Empty(_fixture.Registry.State.Stubs);
        Assert.False(Directory.Exists(Path.GetDirectoryName(BookPath(a))));
    }

    [Fact]
    public async Task ABookThatBecomesAPlaceholderOfAnotherJellyMeshLeavesWithAnIncrementalRead()
    {
        var a = await AddBookServerAsync(Dune());
        await _fixture.SyncAndMaterializeAsync();
        Assert.Single(_fixture.Registry.State.Stubs);

        _fixture.Sync.Client.Items[(TestLibraries.Books, RemoteItemType.Book)][0] = Dune() with { ProviderIds = new Dictionary<string, string?> { ["JellyMesh"] = "s1" } };
        _fixture.Sync.Client.SavedUtc["b1"] = SyncFixture.Start.AddMinutes(1);
        await _fixture.Sync.Sync.RunIncrementalAsync(CancellationToken.None);
        await _fixture.Materializer.MaterializeAsync(false, CancellationToken.None);

        Assert.Empty(_fixture.Registry.State.Stubs);
        Assert.False(Directory.Exists(Path.GetDirectoryName(BookPath(a))));
    }

    [Fact]
    public async Task APlaceholderWithNoWorkIsNeverWrittenOverWithAUrl()
    {
        await AddBookServerAsync(Dune());
        var missing = Guid.NewGuid();
        var relative = FederatedPaths.TypeFolderOf(missing, TestLibraries.Books, TypeFolder.Books) + "/Dune/Dune.epub";
        var path = Path.Combine(_fixture.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, BookPlaceholders.Epub());
        await _fixture.Registry.EnterAsync(CancellationToken.None);
        try
        {
            var stub = new StubEntry
            {
                Id = "kept",
                ServerId = missing,
                Kind = StubKind.Book,
                RelativePath = relative,
                Sources = [new StubSource(missing, "x", "x") { LibraryId = TestLibraries.Books }],
                IdentityKey = "book|x"
            };
            await _fixture.Registry.SaveAsync(_fixture.Registry.State with { Stubs = [stub] }, CancellationToken.None);
        }
        finally
        {
            _fixture.Registry.Exit();
        }

        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);
        await _fixture.Materializer.MaterializeAsync(true, CancellationToken.None);

        Assert.Contains(_fixture.Registry.State.Stubs, stub => stub.Id == "kept");
        Assert.Equal(BookPlaceholders.Epub(), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ABookTheRemoteDropsLeavesWithItsFolder()
    {
        var a = await AddBookServerAsync(Dune());
        await _fixture.SyncAndMaterializeAsync();
        _fixture.Sync.Client.Items[(TestLibraries.Books, RemoteItemType.Book)].Clear();
        _fixture.Sync.Client.Add(TestLibraries.Books, RemoteItemType.Book, Dune() with { Id = "b2", Name = "Other", Path = "/books/Other.pdf" });

        await _fixture.SyncAndMaterializeAsync();

        Assert.False(Directory.Exists(Path.GetDirectoryName(BookPath(a))));
        Assert.True(File.Exists(_fixture.PathIn(a, TestLibraries.Books, "Libri", "Other (1965)", "Other (1965).pdf")));
    }
}
