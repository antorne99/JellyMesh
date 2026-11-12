using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Provider;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Provider;

public class ProviderCollectionsTests
{
    private readonly ProviderFixture _fixture = new();
    private readonly ProviderController _controller;
    private InternalItemsQuery? _boxSetQuery;
    private InternalItemsQuery? _filmQuery;

    public ProviderCollectionsTests()
    {
        _controller = new ProviderController(_fixture.Directory, _fixture.Catalog, new ChangeJournal(), _fixture.Slots)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        WithBoxSets();
        WithFilms(new Movie { Id = ProviderFixture.SharedItem, Path = "/media/film/a.mkv" });
    }

    private static BoxSet BoxSet(string name, string? tmdb, params Guid[] films)
    {
        var boxSet = new BoxSet { Id = Guid.NewGuid(), Name = name, LinkedChildren = films.Select(id => new LinkedChild { ItemId = id }).ToArray() };
        if (tmdb is not null)
        {
            boxSet.SetProviderId(MetadataProvider.Tmdb, tmdb);
        }

        return boxSet;
    }

    private void WithBoxSets(params BoxSet[] boxSets)
    {
        _fixture.Library.Setup(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.BoxSet))))
            .Callback<InternalItemsQuery>(query => _boxSetQuery = query)
            .Returns(boxSets);
    }

    // The films the library query answers with, whatever the identifiers asked for.
    private void WithFilms(params BaseItem[] films)
    {
        _fixture.Library.Setup(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(query => _filmQuery = query)
            .Returns(new QueryResult<BaseItem>(0, films.Length, films));
    }

    private async Task<ProviderCollectionsResponse> ReadAsync(int startIndex = 0, int limit = 100)
        => (await _controller.GetCollections(ProviderFixture.SharedLibrary, startIndex, limit)).Value!;

    [Fact]
    public async Task Each_shared_film_comes_with_the_name_and_tmdb_id_of_its_collection()
    {
        WithBoxSets(BoxSet(" Saga ", "10", ProviderFixture.SharedItem));

        var page = await ReadAsync();

        var entry = Assert.Single(page.Items);
        Assert.Equal(new ProviderCollectionEntry(ProviderFixture.SharedItem.ToString("N"), "Saga", "10"), entry);
        Assert.Equal(1, page.TotalRecordCount);
        Assert.Same(_fixture.Peer, _boxSetQuery!.User);
        Assert.True(_boxSetQuery.Recursive);
    }

    [Fact]
    public async Task Only_members_of_a_collection_are_read_from_the_shared_library_as_the_peer()
    {
        var other = Guid.NewGuid();
        WithBoxSets(BoxSet("Saga", null, ProviderFixture.SharedItem, other));

        await ReadAsync(5, 20);

        Assert.Equal(ProviderFixture.SharedLibrary, _filmQuery!.ParentId);
        Assert.Same(_fixture.Peer, _filmQuery.User);
        Assert.True(_filmQuery.Recursive);
        Assert.Equal([BaseItemKind.Movie], _filmQuery.IncludeItemTypes);
        Assert.Equal(new[] { ProviderFixture.SharedItem, other }.Order(), _filmQuery.ItemIds.Order());
        Assert.False(_filmQuery.IsVirtualItem);
        Assert.Equal(5, _filmQuery.StartIndex);
        Assert.Equal(20, _filmQuery.Limit);
        Assert.True(_filmQuery.EnableTotalRecordCount);
    }

    [Fact]
    public async Task A_library_with_federated_paths_is_read_only_under_its_real_paths()
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/media/film", "/federata/Film");
        var film = _fixture.WithFolder("/media/film");
        WithBoxSets(BoxSet("Saga", null, ProviderFixture.SharedItem));

        await ReadAsync();

        Assert.Equal([film.Id], _filmQuery!.AncestorIds);
    }

    [Fact]
    public async Task A_stub_in_a_collection_never_reaches_a_peer()
    {
        WithBoxSets(BoxSet("Saga", "10", ProviderFixture.SharedItem, ProviderFixture.StubItem));
        WithFilms(
            new Movie { Id = ProviderFixture.SharedItem, Path = "/media/film/a.mkv" },
            new Movie { Id = ProviderFixture.StubItem, Path = "/federata/Film/c/c.strm", ShortcutPath = "http://127.0.0.1:8096/JellyMesh/Play/stub" });

        var page = await ReadAsync();

        Assert.Equal([ProviderFixture.SharedItem.ToString("N")], page.Items.Select(entry => entry.Id));
    }

    [Fact]
    public async Task A_film_in_two_collections_gets_the_first_by_name()
    {
        WithBoxSets(BoxSet("Saga B", "2", ProviderFixture.SharedItem), BoxSet("  Saga A", "1", ProviderFixture.SharedItem));

        var entry = Assert.Single((await ReadAsync()).Items);

        Assert.Equal("Saga A", entry.Name);
        Assert.Equal("1", entry.TmdbId);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    public async Task A_collection_without_a_name_is_left_out(string name)
    {
        WithBoxSets(BoxSet(name, "10", ProviderFixture.SharedItem));

        Assert.Empty((await ReadAsync()).Items);
    }

    [Fact]
    public async Task A_collection_with_a_name_longer_than_256_characters_is_left_out()
    {
        WithBoxSets(BoxSet(new string('x', 257), "10", ProviderFixture.SharedItem));

        Assert.Empty((await ReadAsync()).Items);
    }

    [Theory]
    [InlineData("12a")]
    [InlineData("1234567890123")]
    public async Task A_tmdb_id_that_is_not_up_to_12_digits_is_left_out(string tmdb)
    {
        WithBoxSets(BoxSet("Saga", tmdb, ProviderFixture.SharedItem));

        Assert.Null(Assert.Single((await ReadAsync()).Items).TmdbId);
    }

    [Fact]
    public async Task Without_collections_no_film_is_read()
    {
        var page = await ReadAsync();

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalRecordCount);
        _fixture.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task A_library_that_is_not_shared_is_not_there()
    {
        WithBoxSets(BoxSet("Saga", "10", ProviderFixture.PrivateItem));

        var result = await _controller.GetCollections(ProviderFixture.PrivateLibrary);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.IsType<NotFoundResult>((await _controller.GetCollections(null)).Result);
        _fixture.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task Anyone_but_a_peer_is_refused()
    {
        _fixture.Caller = new User("mario", "auth", "reset");

        Assert.Equal(403, ((StatusCodeResult)(await _controller.GetCollections(ProviderFixture.SharedLibrary)).Result!).StatusCode);
    }

    [Fact]
    public async Task A_page_is_capped_however_much_is_asked()
    {
        WithBoxSets(BoxSet("Saga", "10", ProviderFixture.SharedItem));

        await _controller.GetCollections(ProviderFixture.SharedLibrary, 0, int.MaxValue);

        Assert.Equal(ProviderProtocol.MaxItemsPerPage, _filmQuery!.Limit);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    public async Task A_bad_page_is_refused(int start, int limit)
    {
        Assert.IsType<BadRequestResult>((await _controller.GetCollections(ProviderFixture.SharedLibrary, start, limit)).Result);
    }
}
