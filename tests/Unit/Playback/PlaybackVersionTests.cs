using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Segments;
using Jellyfin.Plugin.JellyMesh.Tests.Segments;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using LinkedChildType = MediaBrowser.Controller.Entities.LinkedChildType;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

// PlaybackInfo per version (Q10, D1, R:versioni §5): a local copy linked with the stubs of two servers, A at 720p and B at
// 1080p, the local copy as the primary.
public class PlaybackVersionTests
{
    private static readonly Guid _localItem = Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static readonly Guid _aItem = Guid.Parse("70000000-0000-0000-0000-00000000000a");
    private static readonly Guid _bItem = Guid.Parse("70000000-0000-0000-0000-00000000000b");
    private static readonly Guid _otherItem = Guid.Parse("70000000-0000-0000-0000-00000000000c");
    private static readonly Guid _serverA = Guid.Parse("a0000000-0000-0000-0000-000000000000");
    private static readonly Guid _serverB = Guid.Parse("b0000000-0000-0000-0000-000000000000");
    private static readonly Guid _serverC = Guid.Parse("c0000000-0000-0000-0000-000000000000");

    private readonly CoordinatorFixture _fixture = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IStubRegistry> _stubs = new();
    private readonly Mock<IAuthorizationContext> _authorization = new();
    private readonly Mock<IFederationResolver> _resolver = new();
    private readonly Mock<IFederationIndex> _index = new();
    private readonly InMemoryConfigurationStore _store = new();
    private readonly User _user = new("viewer", "auth", "reset");
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly List<ServerCatalog> _catalogs = [];
    private readonly Movie _local;
    private readonly Movie _a;
    private readonly Movie _b;
    private readonly Mock<IMediaSegmentManager> _segmentManager = new();
    private readonly StubSegments _segments;

    public PlaybackVersionTests()
    {
        _store.Configuration.FederatedFolderPath = "/federata";
        _library.Setup(library => library.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
        _index.Setup(index => index.Snapshot).Returns(() => new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = IndexHealth.Ok, Servers = _catalogs.ToList() });
        _resolver.Setup(resolver => resolver.Resolve()).Returns(new FederationView(IndexHealth.Ok, 0, [], []));
        _authorization.Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .ReturnsAsync(new AuthorizationInfo { User = _user, Token = PlaybackValues.LocalToken, DeviceId = "device" });

        _local = Item(new Movie { Path = "/media/film/film.mkv" }, _localItem);
        _a = StubItem("a", _aItem, _serverA, 720);
        _b = StubItem("b", _bItem, _serverB, 1080);
        Merged(_local, _a, _b);
        var group = new VersionGroup { Id = "g", IdentityKey = "key", StubIds = ["a", "b"], LocalItemIds = [_localItem], PrimaryStubId = "b" };
        _stubs.Setup(stubs => stubs.FindGroup("a")).Returns(group);
        _stubs.Setup(stubs => stubs.FindGroup("b")).Returns(group);
        _stubs.Setup(stubs => stubs.FindGroupOfLocal(_localItem)).Returns(group);
        Sees(_localItem, _aItem, _bItem);
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([new Folder()]);
        _segments = new StubSegments(
            new StubLookup(_library.Object, _stubs.Object, _store),
            _index.Object,
            _fixture.Registry,
            new StubSegmentsTests.FakeSegmentClient(),
            _store,
            TimeProvider.System,
            NullLogger<StubSegments>.Instance);
    }

    private static string N(Guid id) => id.ToString("N");

    private T Item<T>(T item, Guid id)
        where T : BaseItem
    {
        item.Id = id;
        _items[id] = item;
        return item;
    }

    // A stub of one server, with its quality in the index.
    private Movie StubItem(string stubId, Guid itemId, Guid server, int height)
    {
        var remoteItem = "item-" + stubId;
        _catalogs.Add(new ServerCatalog
        {
            ServerId = server,
            State = ServerSyncState.Complete,
            Items =
            [
                new IndexedItem
                {
                    ItemId = remoteItem,
                    Kind = IndexedItemKind.Movie,
                    LibraryId = "lib",
                    LastSeenUtc = DateTimeOffset.UnixEpoch,
                    Versions = [new IndexedVersion { MediaSourceId = "ms-" + stubId, Height = height, Codec = "h264", BitRate = 1000, VideoRange = "SDR" }]
                }
            ]
        });
        var stub = new StubEntry
        {
            Id = stubId,
            ServerId = server,
            Kind = StubKind.Movie,
            RelativePath = server.ToString("D") + "/Film/x/x - " + stubId + ".strm",
            Sources = [new StubSource(server, remoteItem, "ms-" + stubId)],
            IdentityKey = "key"
        };
        _stubs.Setup(stubs => stubs.Find(stubId)).Returns(stub);
        return Item(new Movie { Path = "/federata/" + stub.RelativePath, ShortcutPath = PlaybackRoutes.StrmUrl(8096, null, stubId, "signature") }, itemId);
    }

    private static void Merged(Video primary, params Video[] alternates)
    {
        primary.LinkedAlternateVersions = alternates.Select(alternate => new LinkedChild { ItemId = alternate.Id, Type = LinkedChildType.LinkedAlternateVersion }).ToArray();
        foreach (var alternate in alternates)
        {
            alternate.SetPrimaryVersionId(primary.Id);
        }
    }

    private void Sees(params Guid[] ids)
    {
        foreach (var id in ids)
        {
            var item = _items[id];
            _library.Setup(library => library.GetItemById<BaseItem>(id, _user)).Returns(item);
        }
    }

    private void Hidden(params Guid[] ids)
    {
        foreach (var id in ids)
        {
            _library.Setup(library => library.GetItemById<BaseItem>(id, _user)).Returns((BaseItem?)null);
        }
    }

    // D17: Jellyfin shows an item that sits in no library to everyone.
    private void InNoLibrary(Guid id)
    {
        _library.Setup(library => library.GetCollectionFolders(It.Is<BaseItem>(item => item.Id == id))).Returns([]);
    }

    private void Admin()
    {
        _user.Permissions.Add(new Permission(PermissionKind.IsAdministrator, true));
    }

    private PlaybackInfoFilter Filter()
    {
        var lookup = new StubLookup(_library.Object, _stubs.Object, _store);
        return new PlaybackInfoFilter(
            lookup,
            _authorization.Object,
            _fixture.Coordinator,
            new PlaybackRefusal(new Mock<ISessionManager>().Object, NullLogger<PlaybackRefusal>.Instance),
            new LocalCopyLookup(_resolver.Object, _library.Object, _stubs.Object),
            new PlaybackVersions(lookup, _stubs.Object, _library.Object, _index.Object, _store),
            new SegmentOffer(_segmentManager.Object, _segments, lookup, _fixture.Sessions));
    }

    private static ActionExecutingContext Context(Guid itemId, params (string Key, object? Value)[] arguments)
    {
        var descriptor = new ControllerActionDescriptor { ControllerName = "MediaInfo", ActionName = "GetPostedPlaybackInfo" };
        var values = new Dictionary<string, object?> { ["itemId"] = itemId };
        foreach (var (key, value) in arguments)
        {
            values[key] = value;
        }

        return new ActionExecutingContext(new ActionContext(new DefaultHttpContext(), new RouteData(), descriptor), [], values!, new object());
    }

    // Runs the filter with stock answering the given sources, and returns what stock was asked and what the client got.
    private async Task<(bool Passed, PlaybackInfoResponse? Response)> RunAsync(ActionExecutingContext context, params MediaSourceInfo[] stock)
    {
        var passed = false;
        var executed = new ActionExecutedContext(context, [], new object())
        {
            Result = new OkObjectResult(new PlaybackInfoResponse { MediaSources = stock, PlaySessionId = "local-session" })
        };
        await Filter().OnActionExecutionAsync(context, () =>
        {
            passed = true;
            return Task.FromResult(executed);
        });
        var result = passed ? executed.Result : context.Result;
        return (passed, (result as ObjectResult)?.Value as PlaybackInfoResponse);
    }

    private static MediaSourceInfo StockSource(Guid id) => new()
    {
        Id = N(id),
        Path = "/media/film/secret-" + N(id) + ".mkv",
        Protocol = MediaProtocol.File,
        Container = "mkv",
        SupportsDirectPlay = true,
        SupportsDirectStream = true,
        SupportsTranscoding = true
    };

    private static int? StatusOf(ActionExecutingContext context) => (context.Result as StatusCodeResult)?.StatusCode;

    private string? RemoteItemAsked() => _fixture.Client.Requests.Select(request => request.ItemId).FirstOrDefault();

    [Fact]
    public async Task Without_a_version_the_local_copy_the_user_sees_starts_under_a_stub()
    {
        var context = Context(_aItem);

        var (passed, response) = await RunAsync(context, StockSource(_localItem));

        Assert.True(passed);
        Assert.Empty(_fixture.Client.Requests);
        Assert.Equal(_localItem, context.ActionArguments["itemId"]);
        Assert.Equal(N(_localItem), context.ActionArguments["mediaSourceId"]);
        var source = Assert.Single(response!.MediaSources);
        Assert.Equal(N(_localItem), source.Id);
        Assert.Null(source.Path);
        Assert.Equal(_localItem, _fixture.Sessions.LocalItemOf("local-session"));
    }

    [Fact]
    public async Task Without_a_version_and_without_the_local_copy_the_best_version_the_user_sees_starts()
    {
        Hidden(_localItem);
        var context = Context(_aItem);

        var (passed, response) = await RunAsync(context);

        Assert.False(passed);
        Assert.Equal("item-b", RemoteItemAsked());
        Assert.Equal(N(_bItem), Assert.Single(response!.MediaSources).Id);
    }

    [Fact]
    public async Task Without_a_version_the_best_version_is_skipped_when_the_user_cannot_see_it()
    {
        Hidden(_localItem, _bItem);
        var context = Context(_aItem);

        var (_, response) = await RunAsync(context);

        Assert.Equal("item-a", RemoteItemAsked());
        Assert.Equal(N(_aItem), Assert.Single(response!.MediaSources).Id);
    }

    [Fact]
    public async Task The_returned_id_is_the_version_served_and_not_the_route()
    {
        Hidden(_localItem);

        var (_, response) = await RunAsync(Context(_aItem));

        Assert.NotEqual(N(_aItem), response!.MediaSources[0].Id);
        Assert.Equal(_bItem, Assert.Single(_fixture.Sessions.All).Identity.ItemId);
    }

    [Fact]
    public async Task A_version_named_in_the_query_wins_over_the_local_copy()
    {
        var context = Context(_bItem, ("mediaSourceId", N(_aItem)));

        var (passed, response) = await RunAsync(context);

        Assert.False(passed);
        Assert.Equal("item-a", RemoteItemAsked());
        Assert.Equal(N(_aItem), Assert.Single(response!.MediaSources).Id);
    }

    [Fact]
    public async Task A_version_named_in_the_body_is_read()
    {
        var context = Context(_bItem, ("playbackInfoDto", new PostedBody { MediaSourceId = N(_aItem), DeviceProfile = PlaybackValues.WebProfile }));

        await RunAsync(context);

        Assert.Equal("item-a", RemoteItemAsked());
    }

    [Fact]
    public async Task The_version_in_the_query_wins_over_the_body()
    {
        var context = Context(_localItem, ("mediaSourceId", N(_aItem)), ("playbackInfoDto", new PostedBody { MediaSourceId = N(_bItem) }));

        await RunAsync(context);

        Assert.Equal("item-a", RemoteItemAsked());
    }

    [Fact]
    public async Task The_route_named_as_its_own_version_is_served()
    {
        var context = Context(_aItem, ("mediaSourceId", N(_aItem)));

        var (_, response) = await RunAsync(context);

        Assert.Equal("item-a", RemoteItemAsked());
        Assert.Equal(N(_aItem), Assert.Single(response!.MediaSources).Id);
    }

    [Fact]
    public async Task A_stub_named_under_the_local_copy_goes_to_its_remote()
    {
        var context = Context(_localItem, ("mediaSourceId", N(_bItem)));

        var (passed, response) = await RunAsync(context);

        Assert.False(passed);
        Assert.Equal("item-b", RemoteItemAsked());
        Assert.Equal(N(_bItem), Assert.Single(response!.MediaSources).Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_version_the_user_cannot_see_is_not_found_and_reaches_no_remote(bool fromLocal)
    {
        Hidden(_bItem);
        var context = Context(fromLocal ? _localItem : _aItem, ("mediaSourceId", N(_bItem)));

        var (passed, _) = await RunAsync(context);

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
        Assert.Empty(_fixture.Client.Requests);
    }

    [Fact]
    public async Task A_hidden_local_copy_named_under_a_stub_is_not_found()
    {
        Hidden(_localItem);
        var context = Context(_aItem, ("mediaSourceId", N(_localItem)));

        var (passed, _) = await RunAsync(context, StockSource(_localItem));

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_version_outside_the_group_is_not_found(bool stub)
    {
        if (stub)
        {
            StubItem("c", _otherItem, _serverC, 2160);
        }
        else
        {
            Item(new Movie { Path = "/media/film/other.mkv" }, _otherItem);
        }

        Sees(_otherItem);
        var context = Context(_aItem, ("mediaSourceId", N(_otherItem)));

        var (passed, _) = await RunAsync(context, StockSource(_otherItem));

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
        Assert.Empty(_fixture.Client.Requests);
    }

    [Fact]
    public async Task A_stub_named_under_an_unrelated_film_is_not_found()
    {
        Item(new Movie { Path = "/media/film/other.mkv" }, _otherItem);
        Sees(_otherItem);
        var context = Context(_otherItem, ("mediaSourceId", N(_aItem)));

        var (passed, _) = await RunAsync(context);

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
        Assert.Empty(_fixture.Client.Requests);
    }

    [Fact]
    public async Task A_version_in_the_group_of_the_registry_is_accepted_before_jellyfin_links_it()
    {
        _local.LinkedAlternateVersions = [];
        _a.SetPrimaryVersionId(null);
        _b.SetPrimaryVersionId(null);
        var context = Context(_aItem, ("mediaSourceId", N(_bItem)));

        await RunAsync(context);

        Assert.Equal("item-b", RemoteItemAsked());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_file_version_of_the_local_copy_is_a_version_of_the_title(bool fromLocal)
    {
        // 12.4 keeps the other files of a local film as local alternates: linked up to the film, not down from it.
        var file = Item(new Movie { Path = "/media/film/film - 720p.mkv", OwnerId = _localItem }, _otherItem);
        file.SetPrimaryVersionId(_localItem);
        Sees(_otherItem);
        var context = Context(fromLocal ? _localItem : _aItem, ("mediaSourceId", N(_otherItem)));

        var (passed, response) = await RunAsync(context, StockSource(_otherItem));

        Assert.True(passed);
        Assert.Empty(_fixture.Client.Requests);
        Assert.Equal(N(_otherItem), context.ActionArguments["mediaSourceId"]);
        Assert.Equal(N(_otherItem), Assert.Single(response!.MediaSources).Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_extra_of_the_local_copy_is_not_a_version_of_the_title(bool fromLocal)
    {
        var trailer = Item(new Movie { Path = "/media/film/trailers/film.mkv", OwnerId = _localItem, ExtraType = ExtraType.Trailer }, _otherItem);
        trailer.SetPrimaryVersionId(_localItem);
        Sees(_otherItem);
        var context = Context(fromLocal ? _localItem : _aItem, ("mediaSourceId", N(_otherItem)));

        var (passed, _) = await RunAsync(context, StockSource(_otherItem));

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Fact]
    public async Task A_stub_named_under_an_extra_of_the_local_copy_is_not_found()
    {
        Item(new Movie { Path = "/media/film/trailers/film.mkv", OwnerId = _localItem, ExtraType = ExtraType.Trailer }, _otherItem);
        Sees(_otherItem);
        var context = Context(_otherItem, ("mediaSourceId", N(_aItem)));

        var (passed, _) = await RunAsync(context);

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
        Assert.Empty(_fixture.Client.Requests);
    }

    [Fact]
    public async Task Without_a_version_an_extra_linked_to_the_local_copy_never_starts()
    {
        var trailer = Item(new Movie { Path = "/media/film/trailers/film.mkv", OwnerId = _localItem, ExtraType = ExtraType.Trailer }, _otherItem);
        Merged(_local, _a, _b, trailer);
        Hidden(_localItem);
        Sees(_otherItem);
        var context = Context(_aItem);

        var (passed, response) = await RunAsync(context, StockSource(_otherItem));

        Assert.False(passed);
        Assert.Equal("item-b", RemoteItemAsked());
        Assert.Equal(N(_bItem), Assert.Single(response!.MediaSources).Id);
    }

    [Fact]
    public async Task The_local_copy_named_under_a_stub_is_left_to_stock()
    {
        var context = Context(_bItem, ("mediaSourceId", N(_localItem)));

        var (passed, response) = await RunAsync(context, StockSource(_localItem));

        Assert.True(passed);
        Assert.Empty(_fixture.Client.Requests);
        Assert.Equal(_localItem, context.ActionArguments["itemId"]);
        Assert.Equal(N(_localItem), Assert.Single(response!.MediaSources).Id);
    }

    [Fact]
    public async Task Under_the_local_copy_without_a_version_stock_answers_for_the_local_copy_only()
    {
        var context = Context(_localItem);

        var (passed, response) = await RunAsync(context, StockSource(_bItem), StockSource(_localItem), StockSource(_aItem));

        Assert.True(passed);
        Assert.Empty(_fixture.Client.Requests);
        Assert.Equal(_localItem, context.ActionArguments["itemId"]);
        Assert.Equal(N(_localItem), context.ActionArguments["mediaSourceId"]);
        var source = Assert.Single(response!.MediaSources);
        Assert.Equal(N(_localItem), source.Id);
        Assert.Equal("/media/film/secret-" + N(_localItem) + ".mkv", source.Path);
    }

    [Fact]
    public async Task The_local_copy_named_as_its_own_version_is_stock()
    {
        var context = Context(_localItem, ("mediaSourceId", N(_localItem)));

        var (passed, response) = await RunAsync(context, StockSource(_localItem));

        Assert.True(passed);
        Assert.Equal(N(_localItem), Assert.Single(response!.MediaSources).Id);
    }

    [Fact]
    public async Task A_film_without_versions_stays_with_stock()
    {
        Item(new Movie { Path = "/media/film/other.mkv" }, _otherItem);
        Sees(_otherItem);
        var context = Context(_otherItem);

        var (passed, response) = await RunAsync(context, StockSource(_otherItem), StockSource(_localItem));

        Assert.True(passed);
        Assert.False(context.ActionArguments.ContainsKey("mediaSourceId"));
        Assert.Equal(2, response!.MediaSources.Count);
    }

    [Fact]
    public async Task A_local_transcode_runs_on_the_version_served_alone()
    {
        Hidden(_localItem);
        _fixture.Client.Answer = (_, _) => Task.FromResult(RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.ServerError));
        var transcoded = new MediaSourceInfo { Id = N(_bItem), TranscodingUrl = "/videos/" + N(_bItem) + "/master.m3u8?MediaSourceId=" + N(_bItem), Path = "http://127.0.0.1/strm" };
        var other = new MediaSourceInfo { Id = N(_aItem), TranscodingUrl = "/videos/" + N(_aItem) + "/master.m3u8", Path = "http://127.0.0.1/strm-a" };
        var context = Context(_aItem, ("playbackInfoDto", new PostedBody { DeviceProfile = PlaybackValues.WebProfile }));

        var (passed, response) = await RunAsync(context, transcoded, other);

        Assert.True(passed);
        Assert.Equal(_bItem, context.ActionArguments["itemId"]);
        Assert.Equal(N(_bItem), context.ActionArguments["mediaSourceId"]);
        var source = Assert.Single(response!.MediaSources);
        Assert.Equal(N(_bItem), source.Id);
        Assert.Null(source.Path);
        Assert.Equal(_bItem, _fixture.Sessions.LocalItemOf("local-session"));
    }

    [Fact]
    public async Task A_stub_whose_server_fails_falls_back_to_the_local_copy_with_its_id()
    {
        _fixture.Registry.Offline.Add(_serverA);
        _fixture.Registry.Offline.Add(_serverB);
        var context = Context(_bItem, ("mediaSourceId", N(_aItem)));

        var (passed, response) = await RunAsync(context, StockSource(_localItem));

        Assert.True(passed);
        Assert.Equal(_localItem, context.ActionArguments["itemId"]);
        Assert.Equal(N(_localItem), Assert.Single(response!.MediaSources).Id);
    }

    [Fact]
    public async Task A_stub_whose_server_fails_falls_back_to_another_version_before_the_local_copy()
    {
        _fixture.Registry.Offline.Add(_serverA);
        var context = Context(_bItem, ("mediaSourceId", N(_aItem)));

        var (passed, response) = await RunAsync(context, StockSource(_localItem));

        Assert.False(passed);
        Assert.Equal(["item-b"], _fixture.Client.Requests.Select(request => request.ItemId));
        Assert.Equal(N(_bItem), Assert.Single(response!.MediaSources).Id);
        var session = Assert.Single(_fixture.Sessions.All);
        Assert.Equal(_bItem, session.Identity.ItemId);
        Assert.Equal("b", session.Identity.StubId);
    }

    [Fact]
    public async Task After_a_fallback_the_segments_offered_are_those_of_the_version_that_serves()
    {
        _fixture.Registry.Offline.Add(_serverA);
        _segmentManager.Setup(manager => manager.HasSegments(_bItem)).Returns(true);
        _segments.Remember(_bItem, new SegmentOrigin(new StubSource(_serverB, "item-b", "ms-b"), 1000));

        var (_, response) = await RunAsync(Context(_aItem, ("mediaSourceId", N(_aItem))));

        var source = Assert.Single(response!.MediaSources);
        Assert.Equal(N(_bItem), source.Id);
        Assert.True(source.HasSegments);
    }

    [Fact]
    public async Task A_remote_source_other_than_the_origin_of_the_segments_offers_none_without_the_same_duration()
    {
        _segmentManager.Setup(manager => manager.HasSegments(_aItem)).Returns(true);
        _segments.Remember(_aItem, new SegmentOrigin(new StubSource(_serverC, "item-c", "ms-c"), 1000));
        _fixture.Client.Answer = (request, _) =>
        {
            var answer = FakePlaybackClient.Hls(request);
            answer.Value!.MediaSources[0].HasSegments = true;
            return Task.FromResult(answer);
        };

        var (_, response) = await RunAsync(Context(_aItem, ("mediaSourceId", N(_aItem))));

        Assert.False(Assert.Single(response!.MediaSources).HasSegments);
    }

    [Theory]
    [InlineData("ms-b", true)]
    [InlineData("ms-other", false)]
    public async Task A_local_transcode_offers_segments_only_from_their_source(string originMediaSource, bool offered)
    {
        Hidden(_localItem);
        _fixture.Client.Answer = (_, _) => Task.FromResult(RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.ServerError));
        _segmentManager.Setup(manager => manager.HasSegments(_bItem)).Returns(true);
        _segments.Remember(_bItem, new SegmentOrigin(new StubSource(_serverB, "item-b", originMediaSource), 1000));
        var transcoded = new MediaSourceInfo { Id = N(_bItem), TranscodingUrl = "/videos/" + N(_bItem) + "/master.m3u8?MediaSourceId=" + N(_bItem), HasSegments = true };
        var context = Context(_aItem, ("playbackInfoDto", new PostedBody { DeviceProfile = PlaybackValues.WebProfile }));

        var (_, response) = await RunAsync(context, transcoded);

        Assert.Equal(offered, Assert.Single(response!.MediaSources).HasSegments);
    }

    [Fact]
    public async Task The_fallback_never_tries_a_version_the_user_cannot_see()
    {
        Hidden(_localItem, _bItem);
        _fixture.Registry.Offline.Add(_serverA);

        var (passed, response) = await RunAsync(Context(_aItem));

        Assert.False(passed);
        Assert.NotEqual(N(_bItem), response?.MediaSources.FirstOrDefault()?.Id);
        Assert.Empty(_fixture.Client.Requests);
        Assert.Empty(_fixture.Client.Opened);
    }

    [Fact]
    public async Task The_fallback_never_tries_a_version_in_no_library()
    {
        _fixture.Registry.Offline.Add(_serverA);
        InNoLibrary(_bItem);
        var context = Context(_localItem, ("mediaSourceId", N(_aItem)));

        await RunAsync(context, StockSource(_localItem));

        Assert.Empty(_fixture.Client.Requests);
        Assert.Empty(_fixture.Client.Opened);
    }

    [Fact]
    public async Task A_local_transcode_after_a_fallback_runs_on_the_version_that_serves()
    {
        Hidden(_localItem);
        _fixture.Registry.Offline.Add(_serverA);
        _fixture.Client.Answer = (_, _) => Task.FromResult(RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.ServerError));
        var transcoded = new MediaSourceInfo { Id = N(_bItem), TranscodingUrl = "/videos/" + N(_bItem) + "/master.m3u8?MediaSourceId=" + N(_bItem), Path = "http://127.0.0.1/strm" };
        var context = Context(_bItem, ("mediaSourceId", N(_aItem)), ("playbackInfoDto", new PostedBody { DeviceProfile = PlaybackValues.WebProfile }));

        var (passed, response) = await RunAsync(context, transcoded);

        Assert.True(passed);
        Assert.Equal(_bItem, context.ActionArguments["itemId"]);
        Assert.Equal(N(_bItem), context.ActionArguments["mediaSourceId"]);
        Assert.Equal(N(_bItem), Assert.Single(response!.MediaSources).Id);
        Assert.Equal(_bItem, _fixture.Sessions.LocalItemOf("local-session"));
    }

    [Fact]
    public async Task A_stub_whose_server_fails_does_not_fall_back_to_a_local_copy_in_no_library()
    {
        _fixture.Registry.Offline.Add(_serverA);
        _fixture.Registry.Offline.Add(_serverB);
        InNoLibrary(_localItem);
        var context = Context(_bItem, ("mediaSourceId", N(_aItem)));

        var (passed, _) = await RunAsync(context, StockSource(_localItem));

        Assert.False(passed);
        Assert.NotEqual(_localItem, context.ActionArguments["itemId"]);
    }

    [Fact]
    public async Task Without_a_version_the_choice_reads_no_library()
    {
        Hidden(_localItem);

        await RunAsync(Context(_aItem));

        _resolver.Verify(resolver => resolver.Resolve(), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_named_version_in_no_library_is_not_found_to_a_non_admin(bool stub)
    {
        var version = stub ? _bItem : _localItem;
        InNoLibrary(version);
        var context = Context(_aItem, ("mediaSourceId", N(version)));

        var (passed, _) = await RunAsync(context, StockSource(_localItem));

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
        Assert.Empty(_fixture.Client.Requests);
    }

    [Fact]
    public async Task A_named_stub_in_no_library_is_served_to_an_admin()
    {
        Admin();
        InNoLibrary(_bItem);

        await RunAsync(Context(_aItem, ("mediaSourceId", N(_bItem))));

        Assert.Equal("item-b", RemoteItemAsked());
    }

    [Fact]
    public async Task A_stub_in_no_library_is_not_found_to_a_non_admin()
    {
        InNoLibrary(_aItem);
        var context = Context(_aItem);

        var (passed, _) = await RunAsync(context, StockSource(_localItem));

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
        Assert.Empty(_fixture.Client.Requests);
    }

    [Fact]
    public async Task Without_a_version_the_local_copy_in_no_library_is_skipped()
    {
        InNoLibrary(_localItem);

        var (_, response) = await RunAsync(Context(_aItem), StockSource(_localItem));

        Assert.Equal("item-b", RemoteItemAsked());
        Assert.Equal(N(_bItem), Assert.Single(response!.MediaSources).Id);
    }

    [Fact]
    public async Task Without_a_version_the_best_stub_in_no_library_is_skipped()
    {
        Hidden(_localItem);
        InNoLibrary(_bItem);

        var (_, response) = await RunAsync(Context(_aItem));

        Assert.Equal("item-a", RemoteItemAsked());
        Assert.Equal(N(_aItem), Assert.Single(response!.MediaSources).Id);
    }

    [Fact]
    public async Task Without_a_version_an_admin_gets_the_local_copy_in_no_library()
    {
        Admin();
        InNoLibrary(_localItem);
        var context = Context(_aItem);

        await RunAsync(context, StockSource(_localItem));

        Assert.Equal(_localItem, context.ActionArguments["itemId"]);
    }

    // The posted body as stock binds it: PlaybackInfoDto is not in a NuGet package, so the filter reads it by name.
    private sealed class PostedBody
    {
        public string? MediaSourceId { get; init; }

        public DeviceProfile? DeviceProfile { get; init; }
    }
}
