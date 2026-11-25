using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Provider;

/// <summary>
/// The full read of a consumer over the pages of the provider, with hidden items next to the real ones. The library
/// behind it counts and pages every item its query does not leave out, as Jellyfin does.
/// </summary>
public sealed class ProviderPagingTests : IDisposable
{
    private const string Real = "/media/film";
    private const string Server = "/vecchia/0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string Hidden = "/media/0f8fad5b-d9cb-469f-a165-70867728950e/Film";

    private readonly ProviderFixture _provider = new();
    private readonly SyncFixture _consumer = new();
    private readonly List<BaseItem> _items = [];
    private readonly Dictionary<Guid, string> _folders = [];

    public ProviderPagingTests()
    {
        _provider.Store.Configuration.FederatedFolderPath = "/nuova";
        _provider.Library.Setup(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>())).Returns<InternalItemsQuery>(query =>
        {
            var found = Find(query);
            return new QueryResult<BaseItem>(query.StartIndex, found.Count, found.Skip(query.StartIndex ?? 0).Take(query.Limit ?? int.MaxValue).ToList());
        });
        _provider.Library.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(query => Find(query).Select(item => item.Id).ToList());
        _provider.Library.Setup(manager => manager.GetItemById(It.IsAny<Guid>())).Returns<Guid>(id => _items.Find(item => item.Id == id));
        _provider.Dto.Setup(service => service.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<DtoOptions>(), It.IsAny<User?>(), It.IsAny<BaseItem?>()))
            .Returns<IReadOnlyList<BaseItem>, DtoOptions, User?, BaseItem?, bool>((items, _, _, _, _) => items.Select(item => new BaseItemDto { Id = item.Id }).ToList());
        _consumer.Client.Rewrite = (query, _) =>
        {
            var page = _provider.Catalog.ReadLibrary(_provider.PeerCaller, ProviderFixture.SharedLibrary, query.Type, query.StartIndex, query.Limit);
            return new RemoteItemPage(page.Items.Select(dto => TestItems.Movie(dto.Id.ToString("N"))).ToList(), page.TotalRecordCount);
        };
    }

    public void Dispose() => _consumer.Dispose();

    [Theory]
    [InlineData(Server + "/Film", "/nuova")]
    [InlineData(Server + "/Film", "")]
    [InlineData(Server + "/f137a2dd21bbc1b99aa5c0f6bf02a805/Film", "/nuova")]
    [InlineData(Server + "/f137a2dd21bbc1b99aa5c0f6bf02a805/Film", "")]
    public async Task A_library_with_the_folder_of_a_server_as_a_path_is_read_in_full_without_it(string old, string root)
    {
        _provider.Store.Configuration.FederatedFolderPath = root;
        WithLocations(Real, old);
        Fill(old);

        var read = await ReadAsync();

        Assert.Equal(RemoteFailure.None, read.Failure);
        Assert.Equal(RealIds(), read.Value!.Select(item => item.ItemId).Order());
        Assert.True(read.Value!.Count > RemoteCatalogReader.PageSize);
    }

    [Theory]
    [InlineData("/nuova", "/media/altro")]
    [InlineData("", "/media/altro")]
    [InlineData("/media/nuova", "/media/altro")]
    [InlineData("/media/nuova", "/vecchia")]
    public async Task A_library_path_that_holds_the_folder_of_a_server_as_another_path_is_read_in_full_without_it(string root, string parent)
    {
        var old = parent + "/0f8fad5b-d9cb-469f-a165-70867728950e/Film";
        _provider.Store.Configuration.FederatedFolderPath = root;
        WithLocations("/media", old);
        Fill(old);

        var read = await ReadAsync();

        Assert.Equal(RemoteFailure.None, read.Failure);
        Assert.Equal(RealIds(), read.Value!.Select(item => item.ItemId).Order());
        _provider.Library.Verify(manager => manager.FindByPath("/nuova", It.IsAny<bool?>()), Times.Never);
    }

    [Fact]
    public void A_library_with_only_folders_of_servers_as_paths_reads_as_empty()
    {
        WithLocations(Server + "/Film", Server + "/f137a2dd21bbc1b99aa5c0f6bf02a805/Film");
        Fill(Server + "/Film");

        var page = _provider.Catalog.ReadLibrary(_provider.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalRecordCount);
        _provider.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public void A_page_of_collections_counts_only_the_films_it_can_send()
    {
        WithLocations(Real, Server + "/Film");
        var real = Add(Real + "/A.mkv");
        var hidden = Add(Server + "/Film/Title/Title - Server.strm");
        _provider.Library.Setup(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.BoxSet)))).Returns(
        [
            new BoxSet { Name = "Saga", LinkedChildren = [new LinkedChild { ItemId = real.Id }, new LinkedChild { ItemId = hidden.Id }] }
        ]);

        var page = _provider.Catalog.ReadCollections(_provider.PeerCaller, ProviderFixture.SharedLibrary, 0, 10);

        Assert.Equal([real.Id.ToString("N")], page.Items.Select(entry => entry.Id));
        Assert.Equal(1, page.TotalRecordCount);
    }

    [Theory]
    [InlineData("/media/0f8fad5b-d9cb-469f-a165-70867728950e/Filmati")]
    [InlineData("/media/Film/0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("/vecchia/Film")]
    [InlineData(Server + "/f137a2dd21bbc1b99aa5c0f6bf02a805")]
    public async Task A_library_path_that_only_looks_like_the_folder_of_a_server_is_still_read(string path)
    {
        WithLocations(Real, path);
        Add(Real + "/A.mkv");
        Add(path + "/B.mkv");

        var read = await ReadAsync();

        Assert.Equal(2, read.Value!.Count);
    }

    [Fact]
    public async Task A_film_under_a_real_path_not_indexed_yet_is_not_dropped_from_the_consumer_for_the_folder_of_a_server()
    {
        WithLocations(Real, Server + "/Film");
        Add(Real + "/A.mkv");
        Add(Server + "/Film/Title/Title - Server.strm");
        _provider.Library.Setup(manager => manager.FindByPath(Real, It.IsAny<bool?>())).Returns((BaseItem?)null);

        var read = await ReadAsync();

        Assert.Equal(RemoteFailure.None, read.Failure);
        Assert.Equal(RealIds(), read.Value!.Select(item => item.ItemId).Order());
    }

    [Fact]
    public void A_path_inside_the_federated_folder_stays_out_while_a_real_path_is_not_indexed_yet()
    {
        WithLocations(Real, "/media/altro", Server + "/Film", "/nuova/Film");
        Add(Real + "/A.mkv");
        Add("/media/altro/B.mkv");
        Add("/nuova/Film/Title/Title - Server.strm");
        _provider.Library.Setup(manager => manager.FindByPath("/media/altro", It.IsAny<bool?>())).Returns((BaseItem?)null);

        var page = _provider.Catalog.ReadLibrary(_provider.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Equal(1, page.TotalRecordCount);
    }

    [Fact]
    public async Task A_page_of_a_library_with_the_folder_of_a_server_as_a_path_costs_no_read_of_its_items()
    {
        WithLocations(Real, Server + "/Film");
        Fill(Server + "/Film");

        await ReadAsync();

        _provider.Library.Verify(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
        _provider.Library.Verify(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task A_hidden_film_below_a_library_path_does_not_fail_the_full_read_and_is_not_sent()
    {
        WithLocations("/media");
        Add(Real + "/A.mkv");
        Add(Hidden + "/Title (2001)/Title (2001).mkv");

        var read = await ReadAsync();

        Assert.Equal(RemoteFailure.None, read.Failure);
        Assert.Equal(RealIds(), read.Value!.Select(item => item.ItemId).Order());
    }

    [Fact]
    public void A_page_that_meets_a_hidden_film_comes_up_short_and_the_next_read_counts_only_what_it_sends()
    {
        WithLocations("/media");
        var real = Add(Real + "/A.mkv");
        Add(Hidden + "/Title/Title.mkv");

        var first = ReadPage();
        var next = ReadPage();

        Assert.Equal([real.Id], first.Items.Select(item => item.Id));
        Assert.Equal(2, first.TotalRecordCount);
        Assert.Equal([real.Id], next.Items.Select(item => item.Id));
        Assert.Equal(1, next.TotalRecordCount);
    }

    [Fact]
    public async Task Hidden_films_over_more_pages_than_a_read_has_attempts_do_not_fail_the_full_read()
    {
        WithLocations("/media");
        for (var i = 0; i < RemoteCatalogReader.PageSize * 6; i++)
        {
            Add((i % 7 == 0 ? Hidden : Real) + "/" + i + "/" + i + ".mkv");
        }

        var read = await ReadAsync();

        Assert.Equal(RemoteFailure.None, read.Failure);
        Assert.Equal(RealIds(), read.Value!.Select(item => item.ItemId).Order());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    public async Task Hidden_films_in_a_row_over_many_pages_do_not_fail_the_full_read(int before)
    {
        WithLocations("/media");
        for (var i = 0; i < before; i++)
        {
            Add(Real + "/a" + i + ".mkv");
        }

        for (var i = 0; i < RemoteCatalogReader.PageSize * 6; i++)
        {
            Add(Hidden + "/" + i + "/" + i + ".mkv");
        }

        Add(Real + "/Last.mkv");

        var read = await ReadAsync();

        Assert.Equal(RemoteFailure.None, read.Failure);
        Assert.Equal(RealIds(), read.Value!.Select(item => item.ItemId).Order());
    }

    [Fact]
    public async Task A_read_after_the_one_that_met_the_hidden_films_costs_only_its_own_pages()
    {
        WithLocations("/media");
        Add(Real + "/A.mkv");
        Add(Hidden + "/Title/Title.mkv");
        await ReadAsync();
        _provider.Library.Invocations.Clear();

        var page = ReadPage();

        Assert.Equal(1, page.TotalRecordCount);
        _provider.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Once);
    }

    [Fact]
    public async Task Reads_one_after_the_other_look_up_the_hidden_films_once()
    {
        WithLocations("/media");
        Add(Real + "/A.mkv");
        Add(Hidden + "/Title/Title.mkv");
        await ReadAsync();
        _provider.Clock.Advance(HiddenItems.CheckInterval);
        _provider.Library.Invocations.Clear();

        ReadPage();
        ReadPage();

        _provider.Library.Verify(manager => manager.GetItemById(It.IsAny<Guid>()), Times.Once);
    }

    [Fact]
    public void A_read_that_meets_a_hidden_film_during_a_search_does_not_search_and_sends_none()
    {
        WithLocations("/media");
        var real = Add(Real + "/A.mkv");
        Add(Hidden + "/Title/Title.mkv");
        var inside = new List<ProviderItemsResponse>();
        _provider.Library.Setup(manager => manager.GetItemsResult(It.Is<InternalItemsQuery>(query => query.Limit > RemoteCatalogReader.PageSize))).Returns<InternalItemsQuery>(query =>
        {
            Add(Hidden + "/Other/Other.mkv");
            inside.Add(ReadPage());
            return new QueryResult<BaseItem>(query.StartIndex, 0, []);
        });

        ReadPage();

        Assert.Equal([real.Id], Assert.Single(inside).Items.Select(item => item.Id));
        Assert.Equal(1, ReadPage().TotalRecordCount);
    }

    [Fact]
    public async Task A_film_no_longer_hidden_is_sent_by_the_next_read()
    {
        WithLocations("/media");
        Add(Real + "/A.mkv");
        var stub = Add(Real + "/B.strm");
        stub.ShortcutPath = "http://127.0.0.1:8096/JellyMesh/Play/stub";
        Assert.Single((await ReadAsync()).Value!);

        stub.ShortcutPath = null;
        _provider.Clock.Advance(HiddenItems.CheckInterval);

        Assert.Equal(2, ReadPage().TotalRecordCount);
    }

    [Fact]
    public async Task A_hidden_film_that_is_gone_is_forgotten_and_the_others_stay_out()
    {
        WithLocations("/media");
        Add(Real + "/A.mkv");
        var gone = Add(Hidden + "/Gone/Gone.mkv");
        var left = Add(Hidden + "/Left/Left.mkv");
        await ReadAsync();
        _items.Remove(gone);
        _provider.Clock.Advance(HiddenItems.CheckInterval);
        ReadPage();
        _provider.Library.Invocations.Clear();
        ReadPage();

        _provider.Library.Verify(manager => manager.GetItemsResult(It.Is<InternalItemsQuery>(query => query.ExcludeItemIds.SequenceEqual(new[] { left.Id }))), Times.Once);
    }

    [Fact]
    public async Task A_hidden_film_found_after_others_is_left_out_too()
    {
        WithLocations("/media");
        Add(Hidden + "/Title/Title.mkv");
        ReadPage();
        Add(Real + "/A.mkv");
        Add(Hidden + "/Other/Other.mkv");

        var read = await ReadAsync();

        Assert.Equal(RemoteFailure.None, read.Failure);
        Assert.Equal(RealIds(), read.Value!.Select(item => item.ItemId).Order());
    }

    [Fact]
    public void More_hidden_films_than_are_kept_still_fail_the_read_and_are_not_sent()
    {
        WithLocations("/media");
        Add(Real + "/A.mkv");
        for (var i = 0; i <= HiddenItems.MaxPerType; i++)
        {
            Add(Hidden + "/" + i + "/" + i + ".mkv");
        }

        ReadPage();
        _provider.Library.Invocations.Clear();
        var page = ReadPage();

        Assert.Equal(RealIds(), page.Items.Select(item => item.Id.ToString("N")));
        Assert.Equal(2, page.TotalRecordCount);
        _provider.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Once);
    }

    [Fact]
    public void A_page_of_collections_leaves_out_the_hidden_films_a_read_met()
    {
        WithLocations("/media");
        var real = Add(Real + "/A.mkv");
        var hidden = Add(Hidden + "/Title/Title.mkv");
        _provider.Library.Setup(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.BoxSet)))).Returns(
        [
            new BoxSet { Name = "Saga", LinkedChildren = [new LinkedChild { ItemId = real.Id }, new LinkedChild { ItemId = hidden.Id }] }
        ]);

        var first = _provider.Catalog.ReadCollections(_provider.PeerCaller, ProviderFixture.SharedLibrary, 0, 10);
        var next = _provider.Catalog.ReadCollections(_provider.PeerCaller, ProviderFixture.SharedLibrary, 0, 10);

        Assert.Equal([real.Id.ToString("N")], first.Items.Select(entry => entry.Id));
        Assert.Equal([real.Id.ToString("N")], next.Items.Select(entry => entry.Id));
        Assert.Equal(1, next.TotalRecordCount);
    }

    private ProviderItemsResponse ReadPage() =>
        _provider.Catalog.ReadLibrary(_provider.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, RemoteCatalogReader.PageSize);

    private async Task<RemoteResult<IReadOnlyList<IndexedItem>>> ReadAsync()
    {
        var server = await _consumer.AddServerAsync("A", TestLibraries.Films);
        return await _consumer.Reader.ReadAsync(server, SyncFixture.Start, CancellationToken.None);
    }

    // Makes the library have these paths, all indexed.
    private void WithLocations(params string[] paths)
    {
        _provider.WithTypedLibrary(ProviderFixture.SharedLibrary, CollectionTypeOptions.movies, paths);
        foreach (var path in paths)
        {
            _folders[_provider.WithFolder(path).Id] = path;
        }
    }

    // More than a page of real films, with a stub of the old folder after each ten.
    private void Fill(string old)
    {
        for (var i = 0; i < RemoteCatalogReader.PageSize + 20; i++)
        {
            Add(Real + "/" + i + ".mkv");
            if (i % 10 == 0)
            {
                Add(old + "/Title " + i + "/Title " + i + " - Server.strm");
            }
        }
    }

    private Movie Add(string path)
    {
        var film = new Movie { Id = Guid.NewGuid(), Path = path };
        _items.Add(film);
        return film;
    }

    private IEnumerable<string> RealIds() =>
        _items.Where(item => item.Path.StartsWith(Real + "/", StringComparison.Ordinal)).Select(item => item.Id.ToString("N")).Order();

    private List<BaseItem> Find(InternalItemsQuery query) => _items
        .Where(item => query.IncludeItemTypes.Length == 0 || query.IncludeItemTypes.Contains(item.GetBaseItemKind()))
        .Where(item => query.ItemIds.Length == 0 || query.ItemIds.Contains(item.Id))
        .Where(item => !query.ExcludeItemIds.Contains(item.Id))
        .Where(item => query.AncestorIds.Length == 0 || query.AncestorIds.Any(id => item.Path.StartsWith(_folders[id] + "/", StringComparison.Ordinal)))
        .ToList();
}
