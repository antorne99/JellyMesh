using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Provider;

public class ProviderCatalogTests
{
    private readonly ProviderFixture _fixture = new();

    [Theory]
    [InlineData("/federata/Film/x/x.strm", null, true)]
    [InlineData("/federata/Serie/x", null, true)]
    [InlineData("/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Film/x/x - Server.strm", null, true)]
    [InlineData("/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Serie/Show/Season 01", null, true)]
    [InlineData("/federata/0f8fad5b-d9cb-469f-a165-70867728950e", null, true)]
    [InlineData("/federata", null, true)]
    [InlineData("/elsewhere/x.strm", "http://127.0.0.1:8096/JellyMesh/Play/stub", true)]
    [InlineData("/elsewhere/x.strm", "http://127.0.0.1:8096/JellyMesh/Play/stub?sig=0123456789abcdef", true)]
    [InlineData("/media/film/x.mkv", null, false)]
    [InlineData("/federata-other/x.mkv", null, false)]
    [InlineData("/elsewhere/x.strm", "http://cdn.example/film.mp4", false)]
    public void Recognises_what_this_server_federates(string path, string? shortcut, bool expected)
    {
        Assert.Equal(expected, _fixture.Catalog.IsFederated(new Movie { Path = path, ShortcutPath = shortcut }));
    }

    [Fact]
    public void A_series_folder_of_stubs_is_federated_though_it_has_no_url()
    {
        Assert.True(_fixture.Catalog.IsFederated(new Series { Path = "/federata/Serie/Show" }));
    }

    [Fact]
    public void A_stub_stays_federated_after_the_folder_setting_changes()
    {
        _fixture.Store.Configuration.FederatedFolderPath = string.Empty;

        Assert.True(_fixture.Catalog.IsFederated(new Movie { Path = "/federata/Film/x.strm", ShortcutPath = "http://127.0.0.1:8096/JellyMesh/Play/stub" }));
    }

    [Fact]
    public void Finds_only_a_shared_real_item()
    {
        var caller = _fixture.PeerCaller;

        Assert.NotNull(_fixture.Catalog.FindVisible(caller, ProviderFixture.SharedItem));
        Assert.Null(_fixture.Catalog.FindVisible(caller, ProviderFixture.PrivateItem));
        Assert.Null(_fixture.Catalog.FindVisible(caller, ProviderFixture.StubItem));
        Assert.Null(_fixture.Catalog.FindVisible(caller, Guid.NewGuid()));
    }

    [Fact]
    public void An_item_the_user_cannot_see_stays_hidden_even_if_shared()
    {
        _fixture.Library.Setup(manager => manager.GetItemById<BaseItem>(ProviderFixture.SharedItem, It.IsAny<User>())).Returns((BaseItem?)null);

        Assert.Null(_fixture.Catalog.FindVisible(_fixture.PeerCaller, ProviderFixture.SharedItem));
    }

    [Theory]
    [InlineData("/federata/Film")]
    [InlineData("/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Film")]
    [InlineData("/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Serie")]
    [InlineData("/federata")]
    public void A_page_of_a_library_with_federated_paths_reads_only_under_its_real_paths(string federated)
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/media/film", federated, "/media/more/");
        var film = _fixture.WithFolder("/media/film");
        var more = _fixture.WithFolder("/media/more");
        var seen = CaptureQuery();

        var page = _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Equal([film.Id, more.Id], seen()!.AncestorIds);
        Assert.Empty(seen()!.ExcludeItemIds);
        Assert.Equal(ProviderFixture.SharedLibrary, seen()!.ParentId);
        Assert.True(seen()!.Recursive);
        Assert.Equal([BaseItemKind.Movie], seen()!.IncludeItemTypes);
        Assert.Equal(1, page.TotalRecordCount);
    }

    [Fact]
    public void A_page_of_a_library_with_only_real_paths_is_not_narrowed()
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/media/film", "/federata-other/Film");
        var seen = CaptureQuery();

        _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Empty(seen()!.AncestorIds);
        Assert.Empty(seen()!.ExcludeItemIds);
        _fixture.Library.Verify(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Theory]
    [InlineData("/federata/Film", "/federata/Serie")]
    [InlineData("/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Film", "/federata/7c9e6679-7425-40de-944b-e07fc1f90ae7/Film")]
    [InlineData("/federata", "/federata/Film")]
    public void A_library_with_only_federated_paths_reads_as_empty(string first, string second)
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, first, second);

        var page = _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalRecordCount);
        _fixture.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);
        Assert.Empty(_fixture.CatalogLogger.Lines);
    }

    [Fact]
    public void A_library_whose_real_paths_are_not_indexed_yet_reads_as_empty()
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/media/film", "/federata/Film");

        var page = _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Equal(0, page.TotalRecordCount);
        _fixture.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);
        AssertDebugLineWithoutPaths();
    }

    [Fact]
    public void A_library_that_is_gone_reads_as_empty()
    {
        _fixture.WithLibrary(ProviderFixture.PrivateLibrary, "/media/film");

        var page = _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Equal(0, page.TotalRecordCount);
        _fixture.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);
        AssertDebugLineWithoutPaths();
    }

    // One Debug line naming the shared library by id and no path.
    private void AssertDebugLineWithoutPaths()
    {
        var line = Assert.Single(_fixture.CatalogLogger.Lines);
        Assert.Equal(LogLevel.Debug, Assert.Single(_fixture.CatalogLogger.Levels));
        Assert.Contains(ProviderFixture.SharedLibrary.ToString(), line, StringComparison.Ordinal);
        Assert.DoesNotContain("/", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_of_a_library_above_the_federated_folder_excludes_everything_under_it_in_the_query()
    {
        _fixture.Store.Configuration.FederatedFolderPath = "/media/federata/";
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/media", "/media/federata/Film");
        var root = _fixture.WithFolder("/media/federata");
        var linked = _fixture.WithFolder("/media/federata/Film");
        var season = Guid.NewGuid();
        _fixture.Library.Setup(manager => manager.GetItemIds(It.Is<InternalItemsQuery>(query => query.AncestorIds.SequenceEqual(new[] { root.Id, linked.Id }) && query.Recursive)))
            .Returns([ProviderFixture.StubItem, season]);
        var seen = CaptureQuery();

        _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Equal([ProviderFixture.StubItem, season], seen()!.ExcludeItemIds);
        Assert.Empty(seen()!.AncestorIds);
    }

    [Fact]
    public void A_page_of_a_library_on_the_filesystem_root_excludes_everything_under_the_federated_folder()
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/", "/federata/Film");
        var root = _fixture.WithFolder("/federata");
        var linked = _fixture.WithFolder("/federata/Film");
        _fixture.Library.Setup(manager => manager.GetItemIds(It.Is<InternalItemsQuery>(query => query.AncestorIds.SequenceEqual(new[] { root.Id, linked.Id }))))
            .Returns([ProviderFixture.StubItem]);
        var seen = CaptureQuery();

        _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Equal([ProviderFixture.StubItem], seen()!.ExcludeItemIds);
        Assert.Empty(seen()!.AncestorIds);
    }

    [Fact]
    public void Without_a_federated_folder_a_page_is_not_narrowed()
    {
        _fixture.Store.Configuration.FederatedFolderPath = string.Empty;
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/media/film", "/federata/Film");
        var seen = CaptureQuery();

        _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Empty(seen()!.AncestorIds);
        Assert.Empty(seen()!.ExcludeItemIds);
    }

    [Fact]
    public void Libraries_with_only_federated_paths_are_not_offered()
    {
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            Library("Mixed", CollectionTypeOptions.movies, "/media/film", "/federata/Film"),
            Library("Flat", CollectionTypeOptions.movies, "/federata/Film"),
            Library("PerServer", CollectionTypeOptions.tvshows, "/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Serie", "/federata/7c9e6679-7425-40de-944b-e07fc1f90ae7/Serie/"),
            Library("Root", CollectionTypeOptions.movies, "/federata"),
            Library("Above", CollectionTypeOptions.movies, "/"),
            Library("Sibling", CollectionTypeOptions.movies, "/federata-other/Film"),
            Library("NoPaths", CollectionTypeOptions.movies)
        ]);

        var names = _fixture.Catalog.ReadLibraries().Select(library => library.Name);

        Assert.Equal(["Mixed", "Above", "Sibling", "NoPaths"], names);
    }

    [Fact]
    public void Without_a_federated_folder_every_library_is_offered()
    {
        _fixture.Store.Configuration.FederatedFolderPath = string.Empty;
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns([Library("Flat", CollectionTypeOptions.movies, "/federata/Film")]);

        Assert.Equal(["Flat"], _fixture.Catalog.ReadLibraries().Select(library => library.Name));
    }

    private static VirtualFolderInfo Library(string name, CollectionTypeOptions type, params string[] locations) =>
        new() { Name = name, ItemId = Guid.NewGuid().ToString(), CollectionType = type, Locations = locations };

    private Func<InternalItemsQuery?> CaptureQuery()
    {
        InternalItemsQuery? seen = null;
        _fixture.Library.Setup(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(query => seen = query)
            .Returns(new QueryResult<BaseItem>(0, 1, [new Movie { Path = "/media/film/a.mkv" }]));
        _fixture.WithDtos();
        return () => seen;
    }

    [Fact]
    public void A_stub_past_the_query_is_dropped_so_the_page_comes_up_short_and_nothing_leaks()
    {
        BaseItem stub = new Movie { Id = Guid.NewGuid(), Path = "/federata/Film/x.strm" };
        _fixture.Library.Setup(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>(0, 2, [stub, new Movie { Id = Guid.NewGuid(), Path = "/media/a.mkv" }]));
        IReadOnlyList<BaseItem>? sent = null;
        _fixture.Dto.Setup(service => service.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<MediaBrowser.Controller.Dto.DtoOptions>(), It.IsAny<User?>(), It.IsAny<BaseItem?>()))
            .Callback<IReadOnlyList<BaseItem>, MediaBrowser.Controller.Dto.DtoOptions, User?, BaseItem?, bool>((items, _, _, _, _) => sent = items)
            .Returns([]);

        var page = _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.DoesNotContain(stub, sent!);
        Assert.Equal(2, page.TotalRecordCount);
    }

    [Fact]
    public void A_lookup_leaves_out_what_the_peer_may_not_reach()
    {
        IReadOnlyList<BaseItem>? sent = null;
        _fixture.Dto.Setup(service => service.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<MediaBrowser.Controller.Dto.DtoOptions>(), It.IsAny<User?>(), It.IsAny<BaseItem?>()))
            .Callback<IReadOnlyList<BaseItem>, MediaBrowser.Controller.Dto.DtoOptions, User?, BaseItem?, bool>((items, _, _, _, _) => sent = items)
            .Returns([]);

        var page = _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem, ProviderFixture.PrivateItem, ProviderFixture.StubItem], RemoteItemType.Movie);

        Assert.Equal(ProviderFixture.SharedItem, Assert.Single(sent!).Id);
        Assert.Equal(1, page.TotalRecordCount);
    }

    [Fact]
    public void The_watch_state_of_this_server_never_reaches_a_peer()
    {
        MediaBrowser.Controller.Dto.DtoOptions? options = null;
        var dto = new BaseItemDto { Id = ProviderFixture.SharedItem, UserData = new UserItemDataDto { Played = true, PlayCount = 3, Key = "k", ItemId = ProviderFixture.SharedItem } };
        _fixture.Dto.Setup(service => service.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<MediaBrowser.Controller.Dto.DtoOptions>(), It.IsAny<User?>(), It.IsAny<BaseItem?>()))
            .Callback<IReadOnlyList<BaseItem>, MediaBrowser.Controller.Dto.DtoOptions, User?, BaseItem?, bool>((_, value, _, _, _) => options = value)
            .Returns([dto]);

        var page = _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie);

        Assert.False(options!.EnableUserData);
        Assert.All(page.Items, item => Assert.Null(item.UserData));
    }

    [Fact]
    public void A_lookup_keeps_to_the_type_asked_for()
    {
        _fixture.WithDtos();

        var page = _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Series);

        Assert.Equal(0, page.TotalRecordCount);
    }

    [Fact]
    public void Quality_comes_from_the_default_video_stream()
    {
        var other = new MediaStream { Type = MediaStreamType.Video, Height = 480, Width = 640, BitRate = 1, Codec = "mpeg2" };
        var main = new MediaStream { Type = MediaStreamType.Video, IsDefault = true, Height = 1080, Width = 1920, BitRate = 5_000_000, Codec = "hevc", ColorTransfer = "smpte2084", ColorPrimaries = "bt2020" };
        _fixture.WithDtos(ProviderFixture.DtoWith("ms", other, main));

        var page = _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie);

        var quality = page.Quality["ms"];
        Assert.Equal(1080, quality.Height);
        Assert.Equal(1920, quality.Width);
        Assert.Equal(5_000_000, quality.BitRate);
        Assert.Equal("hevc", quality.Codec);
        Assert.Equal("HDR", quality.VideoRange);
    }

    [Fact]
    public void A_version_without_video_has_no_quality()
    {
        _fixture.WithDtos(ProviderFixture.DtoWith("ms", new MediaStream { Type = MediaStreamType.Audio }));

        Assert.Empty(_fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie).Quality);
    }

    [Fact]
    public void No_file_path_reaches_a_peer()
    {
        var dto = ProviderFixture.DtoWith("ms", new MediaStream { Type = MediaStreamType.Subtitle, Path = "/media/secret.srt" });
        dto.Chapters = [new ChapterInfo { Name = "Intro", ImagePath = "/config/metadata/secret.jpg" }];
        _fixture.WithDtos(dto);

        _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie);

        Assert.Null(dto.MediaSources[0].Path);
        Assert.Null(dto.MediaSources[0].MediaStreams[0].Path);
        Assert.Null(dto.Chapters[0].ImagePath);
        Assert.Equal("Intro", dto.Chapters[0].Name);
    }
}

public class ProviderControllerTests
{
    private readonly ProviderFixture _fixture = new();
    private readonly ChangeJournal _journal = new();
    private readonly ProviderController _controller;
    private readonly CapabilitiesController _capabilities;

    public ProviderControllerTests()
    {
        var context = new ControllerContext { HttpContext = new DefaultHttpContext() };
        _controller = new ProviderController(_fixture.Directory, _fixture.Catalog, _journal, _fixture.Slots) { ControllerContext = context };
        _capabilities = new CapabilitiesController(_fixture.Directory) { ControllerContext = context };
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "Film", ItemId = ProviderFixture.SharedLibrary.ToString(), CollectionType = CollectionTypeOptions.movies },
            new VirtualFolderInfo { Name = "Private", ItemId = ProviderFixture.PrivateLibrary.ToString(), CollectionType = CollectionTypeOptions.tvshows },
            new VirtualFolderInfo { Name = "Music", ItemId = Guid.NewGuid().ToString(), CollectionType = CollectionTypeOptions.music }
        ]);
        _fixture.WithDtos();
        _fixture.Library.Setup(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>())).Returns(new QueryResult<BaseItem>(0, 0, []));
    }

    private void NotAPeer() => _fixture.Caller = new User("mario", "auth", "reset");

    [Fact]
    public async Task Capabilities_tell_a_peer_the_protocol_version()
    {
        var result = await _capabilities.GetCapabilities();

        Assert.Equal(ProviderProtocol.Version, result.Value!.ProtocolVersion);
    }

    [Fact]
    public async Task Capabilities_answer_anyone_else_like_a_stock_server()
    {
        NotAPeer();

        var result = await _capabilities.GetCapabilities();

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Every_route_is_closed_to_anyone_else()
    {
        NotAPeer();

        Assert.Equal(403, ((StatusCodeResult)(await _controller.GetLibraries()).Result!).StatusCode);
        Assert.Equal(403, ((StatusCodeResult)(await _controller.GetItems(RemoteItemType.Movie, ProviderFixture.SharedLibrary, null)).Result!).StatusCode);
        Assert.Equal(403, ((StatusCodeResult)(await _controller.GetChanges(null)).Result!).StatusCode);
        Assert.Equal(403, ((StatusCodeResult)await _controller.ReleaseStream("play")).StatusCode);
        Assert.Equal(403, ((StatusCodeResult)await _controller.RenewStream("play")).StatusCode);
    }

    [Fact]
    public async Task Libraries_are_only_those_shared()
    {
        var result = await _controller.GetLibraries();

        var library = Assert.Single(result.Value!.Items);
        Assert.Equal(ProviderFixture.SharedLibrary.ToString("N"), library.Id);
        Assert.Equal("movies", library.CollectionType);
    }

    [Fact]
    public async Task A_shared_library_left_with_only_federated_paths_is_neither_listed_nor_read()
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Film");

        Assert.Empty((await _controller.GetLibraries()).Value!.Items);
        Assert.Equal(0, (await _controller.GetItems(RemoteItemType.Movie, ProviderFixture.SharedLibrary, null)).Value!.TotalRecordCount);
        _fixture.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task A_library_that_is_not_shared_is_not_there()
    {
        var result = await _controller.GetItems(RemoteItemType.Movie, ProviderFixture.PrivateLibrary, null);

        Assert.IsType<NotFoundResult>(result.Result);
        _fixture.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task Items_need_a_library_or_identifiers()
    {
        Assert.IsType<NotFoundResult>((await _controller.GetItems(RemoteItemType.Movie, null, null)).Result);
    }

    [Fact]
    public async Task A_page_is_capped_however_much_is_asked()
    {
        InternalItemsQuery? seen = null;
        _fixture.Library.Setup(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(query => seen = query)
            .Returns(new QueryResult<BaseItem>(0, 0, []));

        await _controller.GetItems(RemoteItemType.Movie, ProviderFixture.SharedLibrary, null, 0, int.MaxValue);

        Assert.Equal(ProviderProtocol.MaxItemsPerPage, seen!.Limit);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    public async Task A_bad_page_is_refused(int start, int limit)
    {
        Assert.IsType<BadRequestResult>((await _controller.GetItems(RemoteItemType.Movie, ProviderFixture.SharedLibrary, null, start, limit)).Result);
    }

    [Fact]
    public async Task A_lookup_is_capped()
    {
        var ids = string.Join(',', Enumerable.Range(0, ProviderProtocol.MaxItemsPerPage + 1).Select(_ => Guid.NewGuid().ToString("N")));

        Assert.IsType<BadRequestResult>((await _controller.GetItems(RemoteItemType.Movie, null, ids)).Result);
    }

    [Fact]
    public async Task A_lookup_with_a_bad_identifier_is_refused()
    {
        Assert.IsType<BadRequestResult>((await _controller.GetItems(RemoteItemType.Movie, null, "not-a-guid")).Result);
    }

    [Fact]
    public async Task The_feed_starts_with_a_position()
    {
        var result = await _controller.GetChanges(null);

        Assert.True(result.Value!.Reset);
        Assert.NotEmpty(result.Value.Cursor);
    }

    [Fact]
    public async Task The_feed_shows_only_what_the_peer_may_see()
    {
        var cursor = (await _controller.GetChanges(null)).Value!.Cursor;
        _journal.Record(ProviderFixture.SharedItem);
        _journal.Record(ProviderFixture.PrivateItem);
        _journal.Record(ProviderFixture.StubItem);
        _journal.Record(Guid.NewGuid());

        var result = await _controller.GetChanges(cursor);

        var change = Assert.Single(result.Value!.Changes);
        Assert.Equal(ProviderFixture.SharedItem.ToString("N"), change.Id);
        Assert.Equal("Movie", change.Type);
        Assert.Equal([ProviderFixture.SharedLibrary.ToString("N")], change.LibraryIds);
    }

    [Fact]
    public async Task A_change_of_the_share_resets_the_feed()
    {
        var cursor = (await _controller.GetChanges(null)).Value!.Cursor;
        _fixture.Store.Configuration.Peers[0].SharedLibraryIds.Add(ProviderFixture.PrivateLibrary.ToString("N"));

        Assert.True((await _controller.GetChanges(cursor)).Value!.Reset);
    }

    [Fact]
    public async Task A_bad_feed_limit_is_refused()
    {
        Assert.IsType<BadRequestResult>((await _controller.GetChanges(null, 0)).Result);
    }

    [Fact]
    public async Task Renewal_keeps_the_slot_of_the_caller()
    {
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "play")!.Dispose();
        _fixture.Clock.Advance(StreamSlots.IdleTimeout - TimeSpan.FromSeconds(1));

        var result = await _controller.RenewStream("play");
        _fixture.Clock.Advance(TimeSpan.FromSeconds(2));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task Renewing_a_slot_that_is_gone_takes_none()
    {
        Assert.IsType<NoContentResult>(await _controller.RenewStream("gone"));

        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task Release_frees_the_slot_of_the_caller_only()
    {
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "play")!.Dispose();
        var other = Guid.NewGuid();
        _fixture.Slots.TryEnter(other, 1, "play")!.Dispose();

        await _controller.ReleaseStream("play");

        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
        Assert.Equal(1, _fixture.Slots.Count(other));
    }
}

public class PeerAdminControllerTests
{
    private readonly ProviderFixture _fixture = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly User _admin = new("admin", "auth", "reset");
    private readonly PeerAdminController _controller;

    public PeerAdminControllerTests()
    {
        _admin.Permissions.Add(new Permission(PermissionKind.IsAdministrator, true));
        _users.Setup(users => users.GetUserById(_fixture.Peer.Id)).Returns(_fixture.Peer);
        _users.Setup(users => users.GetUserById(_admin.Id)).Returns(_admin);
        _users.Setup(users => users.GetUsers()).Returns([_fixture.Peer, _admin]);
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "Film", ItemId = ProviderFixture.SharedLibrary.ToString(), CollectionType = CollectionTypeOptions.movies }
        ]);
        _fixture.Store.Configuration.Peers.Clear();
        _controller = new PeerAdminController(_fixture.Store, _users.Object, _fixture.Catalog, _fixture.Slots);
    }

    private static SetPeerRequest Request(int limit = 2) => new([ProviderFixture.SharedLibrary.ToString("N")], limit);

    [Fact]
    public void Connects_a_non_admin_user()
    {
        Assert.IsType<NoContentResult>(_controller.SetPeer(_fixture.Peer.Id, Request()));

        var peer = Assert.Single(_fixture.Store.Configuration.Peers);
        Assert.Equal(2, peer.MaxStreams);
        Assert.Equal([ProviderFixture.SharedLibrary.ToString("N")], peer.SharedLibraryIds);
    }

    [Fact]
    public void Saving_again_replaces_what_is_shared()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request(2));
        _controller.SetPeer(_fixture.Peer.Id, new SetPeerRequest([], 5));

        var peer = Assert.Single(_fixture.Store.Configuration.Peers);
        Assert.Equal(5, peer.MaxStreams);
        Assert.Empty(peer.SharedLibraryIds);
    }

    [Fact]
    public void An_administrator_cannot_be_a_peer()
    {
        Assert.IsType<BadRequestResult>(_controller.SetPeer(_admin.Id, Request()));
        Assert.Empty(_fixture.Store.Configuration.Peers);
    }

    [Fact]
    public void An_unknown_user_is_refused()
    {
        Assert.IsType<NotFoundResult>(_controller.SetPeer(Guid.NewGuid(), Request()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_limit_must_be_positive(int limit)
    {
        Assert.IsType<BadRequestResult>(_controller.SetPeer(_fixture.Peer.Id, Request(limit)));
    }

    [Fact]
    public void A_library_with_only_federated_paths_is_dropped_from_a_save_and_reported()
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/federata/Film");

        Assert.Empty(_controller.GetOptions().Value!.Libraries);
        var result = Assert.IsType<OkObjectResult>(_controller.SetPeer(_fixture.Peer.Id, Request()));

        Assert.Equal([ProviderFixture.SharedLibrary.ToString("N")], Assert.IsType<SetPeerResponse>(result.Value).DroppedLibraryIds);
        Assert.Empty(Assert.Single(_fixture.Store.Configuration.Peers).SharedLibraryIds);
    }

    [Fact]
    public void An_unknown_library_is_dropped_and_what_is_offered_is_saved()
    {
        TwoLibraries();
        var unknown = Guid.NewGuid().ToString("N");

        var result = Assert.IsType<OkObjectResult>(_controller.SetPeer(_fixture.Peer.Id, new SetPeerRequest([unknown, ProviderFixture.PrivateLibrary.ToString("N")], 1)));

        Assert.Equal([unknown], Assert.IsType<SetPeerResponse>(result.Value).DroppedLibraryIds);
        Assert.Equal([ProviderFixture.PrivateLibrary.ToString("N")], Assert.Single(_fixture.Store.Configuration.Peers).SharedLibraryIds);
    }

    [Fact]
    public void A_saved_library_no_longer_offered_is_dropped_on_the_next_save_and_reported()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, Both());
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "Altra", ItemId = ProviderFixture.PrivateLibrary.ToString(), CollectionType = CollectionTypeOptions.movies }
        ]);

        var result = Assert.IsType<OkObjectResult>(_controller.SetPeer(_fixture.Peer.Id, OnlyPrivate()));

        Assert.Equal([ProviderFixture.SharedLibrary.ToString("N")], Assert.IsType<SetPeerResponse>(result.Value).DroppedLibraryIds);
        Assert.Equal([ProviderFixture.PrivateLibrary.ToString("N")], Assert.Single(_fixture.Store.Configuration.Peers).SharedLibraryIds);
    }

    [Fact]
    public void A_save_with_nothing_to_drop_reports_nothing()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, Both());

        Assert.IsType<NoContentResult>(_controller.SetPeer(_fixture.Peer.Id, OnlyPrivate()));
    }

    [Fact]
    public void Dropping_a_library_no_longer_offered_frees_the_slots_on_it_even_when_it_is_sent_again()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request());
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "play", ProviderFixture.SharedItem)!.Dispose();
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/federata/Film");

        _controller.SetPeer(_fixture.Peer.Id, Request());

        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public void Options_offer_only_non_admin_users()
    {
        var options = _controller.GetOptions().Value!;

        Assert.Equal(_fixture.Peer.Id, Assert.Single(options.Users).Id);
    }

    [Fact]
    public void The_list_shows_the_streams_in_use()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request());
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "play")!.Dispose();

        var peers = (IReadOnlyList<PeerStatus>)_controller.GetPeers().Value!;

        Assert.Equal(1, Assert.Single(peers).ActiveStreams);
    }

    [Fact]
    public void Removing_a_peer_leaves_the_user_an_ordinary_one()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request());

        Assert.IsType<NoContentResult>(_controller.RemovePeer(_fixture.Peer.Id));

        Assert.Empty(_fixture.Store.Configuration.Peers);
        Assert.Null(_fixture.Directory.Find(_fixture.Peer.Id));
    }

    [Fact]
    public void Removing_a_peer_frees_its_slots_at_once()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request());
        using var running = _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "play");

        _controller.RemovePeer(_fixture.Peer.Id);

        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public void A_higher_limit_frees_nothing()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request());
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "play", ProviderFixture.SharedItem)!.Dispose();

        _controller.SetPeer(_fixture.Peer.Id, Request(3));

        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public void A_lower_limit_ends_what_no_longer_fits_and_keeps_the_rest_counted()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request(3));
        var cut = new List<string>();
        using var oldest = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "oldest", ProviderFixture.SharedItem, () => cut.Add("oldest"));
        using var middle = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "middle", ProviderFixture.SharedItem, () => cut.Add("middle"));
        using var newest = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "newest", ProviderFixture.SharedItem, () => cut.Add("newest"));

        _controller.SetPeer(_fixture.Peer.Id, Request(1));

        Assert.Equal(["middle", "newest"], cut.Order(StringComparer.Ordinal));
        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
        Assert.Equal(["middle", "newest"], _fixture.Slots.TakeClosing().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_limit_lowered_to_what_is_running_ends_nothing()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request(3));
        var cut = false;
        using var one = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "one", ProviderFixture.SharedItem, () => cut = true);
        using var two = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "two", ProviderFixture.SharedItem, () => cut = true);

        _controller.SetPeer(_fixture.Peer.Id, Request(2));

        Assert.False(cut);
        Assert.Equal(2, _fixture.Slots.Count(_fixture.Peer.Id));
        Assert.Empty(_fixture.Slots.TakeClosing());
    }

    [Fact]
    public void A_save_that_lowers_the_limit_and_takes_a_library_away_counts_what_was_lost_first()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, new SetPeerRequest(Both().SharedLibraryIds, 3));
        var cut = new List<string>();
        using var lost = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "lost", ProviderFixture.SharedItem, () => cut.Add("lost"));
        using var older = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "older", ProviderFixture.PrivateItem, () => cut.Add("older"));
        using var newer = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "newer", ProviderFixture.PrivateItem, () => cut.Add("newer"));

        _controller.SetPeer(_fixture.Peer.Id, new SetPeerRequest(OnlyPrivate().SharedLibraryIds, 1));

        Assert.Equal(["lost", "newer"], cut.Order(StringComparer.Ordinal));
        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public void A_save_that_takes_a_library_away_can_leave_nothing_more_to_cut_for_the_lower_limit()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, new SetPeerRequest(Both().SharedLibraryIds, 3));
        var cut = new List<string>();
        using var lost = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "lost", ProviderFixture.SharedItem, () => cut.Add("lost"));
        using var kept = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "kept", ProviderFixture.PrivateItem, () => cut.Add("kept"));

        _controller.SetPeer(_fixture.Peer.Id, new SetPeerRequest(OnlyPrivate().SharedLibraryIds, 1));

        Assert.Equal(["lost"], cut);
        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public void Taking_a_library_away_frees_the_slots_on_it()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request());
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "play", ProviderFixture.SharedItem)!.Dispose();

        _controller.SetPeer(_fixture.Peer.Id, new SetPeerRequest([], 2));

        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public void Taking_a_library_away_leaves_the_slot_of_what_is_still_shared_counted()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, Both());
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "lost", ProviderFixture.SharedItem)!.Dispose();
        using var kept = _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "kept", ProviderFixture.PrivateItem);

        _controller.SetPeer(_fixture.Peer.Id, OnlyPrivate());

        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
        Assert.NotNull(_fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "another", ProviderFixture.PrivateItem));
        Assert.Null(_fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "over", ProviderFixture.PrivateItem));
    }

    [Fact]
    public void An_added_library_frees_nothing()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, Request());
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "play", ProviderFixture.SharedItem)!.Dispose();

        _controller.SetPeer(_fixture.Peer.Id, Both());

        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public void Removing_a_peer_frees_all_its_slots()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, Both());
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "one", ProviderFixture.SharedItem)!.Dispose();
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "two", ProviderFixture.PrivateItem)!.Dispose();

        _controller.RemovePeer(_fixture.Peer.Id);

        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public void Saving_the_same_grant_again_leaves_the_slots()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request());
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "play")!.Dispose();

        _controller.SetPeer(_fixture.Peer.Id, Request());

        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    private void TwoLibraries()
    {
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "Film", ItemId = ProviderFixture.SharedLibrary.ToString(), CollectionType = CollectionTypeOptions.movies },
            new VirtualFolderInfo { Name = "Altra", ItemId = ProviderFixture.PrivateLibrary.ToString(), CollectionType = CollectionTypeOptions.movies }
        ]);
    }

    private static SetPeerRequest Both() => new([ProviderFixture.SharedLibrary.ToString("N"), ProviderFixture.PrivateLibrary.ToString("N")], 2);

    private static SetPeerRequest OnlyPrivate() => new([ProviderFixture.PrivateLibrary.ToString("N")], 2);

    [Fact]
    public void Removing_a_peer_cuts_all_it_is_playing()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request());
        var cut = 0;
        using var first = _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "one", ProviderFixture.SharedItem, () => cut++);
        using var second = _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "two", ProviderFixture.PrivateItem, () => cut++);

        _controller.RemovePeer(_fixture.Peer.Id);

        Assert.Equal(2, cut);
    }

    [Fact]
    public void Taking_a_library_away_cuts_only_what_plays_from_it()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, Both());
        var cut = new List<string>();
        using var lost = _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "lost", ProviderFixture.SharedItem, () => cut.Add("lost"));
        using var kept = _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "kept", ProviderFixture.PrivateItem, () => cut.Add("kept"));

        _controller.SetPeer(_fixture.Peer.Id, OnlyPrivate());

        Assert.Equal(["lost"], cut);
    }

    [Fact]
    public void An_item_also_in_a_library_still_shared_is_not_cut()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, Both());
        var item = new MediaBrowser.Controller.Entities.Movies.Movie { Path = "/media/film/both.mkv", Id = Guid.NewGuid() };
        _fixture.Library.Setup(manager => manager.GetItemById<MediaBrowser.Controller.Entities.BaseItem>(item.Id, It.IsAny<User>())).Returns(item);
        _fixture.Library.Setup(manager => manager.GetCollectionFolders(item)).Returns(
        [
            new MediaBrowser.Controller.Entities.Folder { Id = ProviderFixture.SharedLibrary },
            new MediaBrowser.Controller.Entities.Folder { Id = ProviderFixture.PrivateLibrary }
        ]);
        var cut = false;
        using var running = _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "both", item.Id, () => cut = true);

        _controller.SetPeer(_fixture.Peer.Id, OnlyPrivate());

        Assert.False(cut);
        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public void A_higher_limit_an_added_library_or_the_same_grant_cuts_nothing()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, Request());
        var cut = false;
        using var running = _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "play", ProviderFixture.SharedItem, () => cut = true);

        _controller.SetPeer(_fixture.Peer.Id, Request(3));
        _controller.SetPeer(_fixture.Peer.Id, Request(3));
        _controller.SetPeer(_fixture.Peer.Id, Both());

        Assert.False(cut);
    }

    [Fact]
    public void Removing_an_unknown_peer_says_so()
    {
        Assert.IsType<NotFoundResult>(_controller.RemovePeer(Guid.NewGuid()));
    }
}

public class ProviderConfigurationTests
{
    [Fact]
    public void A_transcode_closes_at_the_expiry_of_its_slot_by_default()
    {
        Assert.True(new PluginConfiguration().CloseTranscodeAtSlotExpiry);
    }

    [Fact]
    public void A_save_from_the_stock_endpoint_keeps_the_peers()
    {
        var current = new PluginConfiguration();
        var peer = new ProviderPeerConfig { UserId = Guid.NewGuid(), MaxStreams = 2 };
        current.Peers.Add(peer);
        var incoming = new PluginConfiguration();

        incoming.AdoptServersFrom(current);

        Assert.Same(peer, Assert.Single(incoming.Peers));
    }

    [Fact]
    public void Peers_never_go_to_the_browser_through_the_configuration()
    {
        var configuration = new PluginConfiguration();
        configuration.Peers.Add(new ProviderPeerConfig { UserId = Guid.NewGuid(), MaxStreams = 2 });

        var json = System.Text.Json.JsonSerializer.Serialize(configuration);

        Assert.DoesNotContain("Peers", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Peers_survive_the_xml_of_the_configuration()
    {
        var configuration = new PluginConfiguration();
        var peer = new ProviderPeerConfig { UserId = Guid.NewGuid(), MaxStreams = 3 };
        peer.SharedLibraryIds.Add("library");
        configuration.Peers.Add(peer);
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));

        using var stream = new System.IO.MemoryStream();
        serializer.Serialize(stream, configuration);
        stream.Position = 0;
        var read = (PluginConfiguration)serializer.Deserialize(stream)!;

        var restored = Assert.Single(read.Peers);
        Assert.Equal(peer.UserId, restored.UserId);
        Assert.Equal(3, restored.MaxStreams);
        Assert.Equal(["library"], restored.SharedLibraryIds);
    }
}

public class ChangeFeedRecorderTests
{
    private readonly ProviderFixture _fixture = new();
    private readonly ChangeJournal _journal = new();

    private async Task<string> StartAsync()
    {
        await new ChangeFeedRecorder(_fixture.Library.Object, _fixture.Catalog, _journal).StartAsync(default);
        return _journal.Read(null, "share", 10).Cursor;
    }

#pragma warning disable CA1849 // These are plain events: there is nothing to await.
    private void RaiseAdded(BaseItem item)
    {
        _fixture.Library.Raise(manager => manager.ItemAdded += null, _fixture.Library.Object, new ItemChangeEventArgs { Item = item });
    }

    private void RaiseUpdated(BaseItem item)
    {
        _fixture.Library.Raise(manager => manager.ItemUpdated += null, _fixture.Library.Object, new ItemChangeEventArgs { Item = item });
    }
#pragma warning restore CA1849

    [Fact]
    public async Task Records_an_added_film()
    {
        var cursor = await StartAsync();
        var movie = new Movie { Id = Guid.NewGuid(), Path = "/media/a.mkv" };

        RaiseAdded(movie);

        Assert.Equal([movie.Id], _journal.Read(cursor, "share", 10).ItemIds);
    }

    [Fact]
    public async Task Records_an_updated_episode()
    {
        var cursor = await StartAsync();
        var episode = new Episode { Id = Guid.NewGuid(), Path = "/media/a.mkv" };

        RaiseUpdated(episode);

        Assert.Equal([episode.Id], _journal.Read(cursor, "share", 10).ItemIds);
    }

    [Fact]
    public async Task A_stub_never_enters_the_feed()
    {
        var cursor = await StartAsync();

        RaiseAdded(new Movie { Id = Guid.NewGuid(), Path = "/federata/Film/x.strm" });

        Assert.Empty(_journal.Read(cursor, "share", 10).ItemIds);
    }

    [Fact]
    public async Task What_is_not_in_a_catalog_stays_out()
    {
        var cursor = await StartAsync();

        RaiseAdded(new Folder { Id = Guid.NewGuid(), Path = "/media" });

        Assert.Empty(_journal.Read(cursor, "share", 10).ItemIds);
    }

    [Fact]
    public async Task After_stopping_nothing_is_recorded()
    {
        var recorder = new ChangeFeedRecorder(_fixture.Library.Object, _fixture.Catalog, _journal);
        await recorder.StartAsync(default);
        await recorder.StopAsync(default);
        var cursor = _journal.Read(null, "share", 10).Cursor;

        RaiseAdded(new Movie { Id = Guid.NewGuid(), Path = "/media/a.mkv" });

        Assert.Empty(_journal.Read(cursor, "share", 10).ItemIds);
    }
}
