using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The stub providers on a book: a book has no .nfo, so its fields come from the plugin at the first refresh (D62).
public sealed class BookProviderTests : IDisposable
{
    private readonly StubFixture _fixture = new();

    public BookProviderTests() => _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.Books, "Libri", "books"));

    public void Dispose() => _fixture.Dispose();

    private StubBeforeProbeProvider Before() => new(_fixture.Registry, _fixture.Sync.Store, _fixture.Technical);

    private StubAfterProbeProvider After() => new(_fixture.Registry, _fixture.Sync.Store, _fixture.Technical);

    private static MetadataRefreshOptions Options() => new(new Mock<IDirectoryService>().Object);

    private async Task<string> BookStubAsync()
    {
        _fixture.Sync.Client.Add(TestLibraries.Books, RemoteItemType.Book, new RemoteItem
        {
            Id = "b1",
            Name = "Dune",
            ProductionYear = 1965,
            Overview = "Spice",
            Path = "/books/Dune/Dune.epub",
            Genres = ["Fantascienza"],
            Studios = [new RemoteNamedItem("Chilton")],
            SeriesName = "Le cronache di Dune",
            People = [new RemotePerson("Frank Herbert", null, "Author")],
            ProviderIds = new Dictionary<string, string?> { ["Isbn"] = "9780441013593" }
        });
        var a = await _fixture.Sync.AddServerAsync("A", TestLibraries.Books);
        await _fixture.SyncAndMaterializeAsync();
        return _fixture.PathIn(a, TestLibraries.Books, "Libri", "Dune (1965)", "Dune (1965).epub");
    }

    [Fact]
    public void TheFirstRunsOnBooksBeforeTheRefreshAndOnALockedItem()
    {
        Assert.IsAssignableFrom<ICustomMetadataProvider<Book>>(Before());
        Assert.IsAssignableFrom<IPreRefreshProvider>(Before());
        Assert.IsAssignableFrom<IForcedProvider>(Before());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ANewBookGetsItsFieldsAndTheMarkFromTheIndexAndIsLockedOnlyWithProtection(bool isProtected)
    {
        _fixture.Sync.Store.Configuration.MetadataProtection = isProtected;
        var path = await BookStubAsync();
        var item = new Book { Id = Guid.NewGuid(), Path = path, Name = "Dune (1965)" };

        var result = await Before().FetchAsync(item, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.MetadataImport, result);
        Assert.Equal(("Dune", 1965, "Spice", "Le cronache di Dune"), (item.Name, item.ProductionYear, item.Overview, item.SeriesName));
        Assert.Equal(["Fantascienza"], item.Genres);
        Assert.Equal(["Chilton"], item.Studios);
        Assert.Equal("9780441013593", item.GetProviderId("Isbn"));
        Assert.Equal(Assert.Single(_fixture.Registry.State.Stubs).Id, item.GetProviderId(FederatedBookMark.ProviderId));
        Assert.Equal(isProtected, item.IsLocked);
        Assert.Empty(item.LockedFields);
    }

    // The provider leaves the authors to the next materializer run, which still writes them after the fields it wrote.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheAuthorsLandOnABookAfterTheProviderWithOrWithoutProtection(bool isProtected)
    {
        _fixture.Sync.Store.Configuration.MetadataProtection = isProtected;
        var path = await BookStubAsync();
        var item = new Book { Id = Guid.NewGuid(), Path = path, Name = "Dune (1965)" };
        await Before().FetchAsync(item, Options(), CancellationToken.None);
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);

        await _fixture.SyncAndMaterializeAsync();

        var people = Assert.Single(_fixture.Library.Invocations, call => call.Method.Name == nameof(ILibraryManager.UpdatePeopleAsync) && call.Arguments[0] == item);
        var author = Assert.Single((IEnumerable<PersonInfo>)people.Arguments[1]);
        Assert.Equal(("Frank Herbert", PersonKind.Author), (author.Name, author.Type));
        Assert.Equal(isProtected, item.IsLocked);
    }

    [Fact]
    public async Task AnUnchangedBookIsNotSavedAgain()
    {
        var path = await BookStubAsync();
        var item = new Book { Id = Guid.NewGuid(), Path = path };
        await Before().FetchAsync(item, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.None, await Before().FetchAsync(item, Options(), CancellationToken.None));
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("unknown")]
    [InlineData("no folder")]
    public async Task ABookThatIsNotAKnownPlaceholderIsLeftAlone(string what)
    {
        var path = await BookStubAsync();
        var item = new Book
        {
            Id = Guid.NewGuid(),
            Name = "Mine",
            Path = what switch
            {
                "outside" => "/media/books/Dune (1965).epub",
                "unknown" => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "Other.epub"),
                _ => path
            }
        };
        if (what == "no folder")
        {
            _fixture.Sync.Store.Configuration.FederatedFolderPath = string.Empty;
        }

        Assert.Equal(ItemUpdateType.None, await Before().FetchAsync(item, Options(), CancellationToken.None));
        Assert.Equal(("Mine", false), (item.Name, item.IsLocked));
        Assert.Empty(item.ProviderIds);
    }

    [Fact]
    public async Task TheSecondLeavesABookAlone()
    {
        var path = await BookStubAsync();
        var item = new Book { Id = Guid.NewGuid(), Path = path, Name = "Mine" };

        Assert.Equal(ItemUpdateType.None, await After().FetchAsync(item, Options(), CancellationToken.None));
        Assert.Equal("Mine", item.Name);
        _fixture.Streams.Verify(streams => streams.SaveMediaStreams(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<MediaStream>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
