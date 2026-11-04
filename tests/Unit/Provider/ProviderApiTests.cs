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
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Provider;

public class ProviderCatalogTests
{
    private readonly ProviderFixture _fixture = new();

    [Theory]
    [InlineData("/federata/Film/x/x.strm", null, true)]
    [InlineData("/federata/Serie/x", null, true)]
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

    [Fact]
    public void A_page_excludes_stubs_in_the_query_so_the_total_is_true()
    {
        var folder = new Folder { Id = Guid.NewGuid() };
        var stubs = new List<Guid> { ProviderFixture.StubItem };
        _fixture.Library.Setup(manager => manager.FindByPath("/federata/Film", true)).Returns(folder);
        _fixture.Library.Setup(manager => manager.GetItemIds(It.Is<InternalItemsQuery>(query => query.AncestorIds.Contains(folder.Id)))).Returns(stubs);
        InternalItemsQuery? seen = null;
        _fixture.Library.Setup(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(query => seen = query)
            .Returns(new QueryResult<BaseItem>(0, 1, [new Movie { Path = "/media/film/a.mkv" }]));
        _fixture.WithDtos();

        var page = _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Contains(ProviderFixture.StubItem, seen!.ExcludeItemIds);
        Assert.Equal(ProviderFixture.SharedLibrary, seen.ParentId);
        Assert.True(seen.Recursive);
        Assert.Equal([BaseItemKind.Movie], seen.IncludeItemTypes);
        Assert.Equal(1, page.TotalRecordCount);
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
        _fixture.WithDtos(dto);

        _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie);

        Assert.Null(dto.MediaSources[0].Path);
        Assert.Null(dto.MediaSources[0].MediaStreams[0].Path);
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
    public void An_unknown_library_is_refused()
    {
        Assert.IsType<BadRequestResult>(_controller.SetPeer(_fixture.Peer.Id, new SetPeerRequest([Guid.NewGuid().ToString("N")], 1)));
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
    public void Removing_an_unknown_peer_says_so()
    {
        Assert.IsType<NotFoundResult>(_controller.RemovePeer(Guid.NewGuid()));
    }
}

public class ProviderConfigurationTests
{
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
