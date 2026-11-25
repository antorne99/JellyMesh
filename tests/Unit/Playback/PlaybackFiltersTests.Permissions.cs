using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

// Row 458: every stock route that names a stub of a new type wants a user (401) who sees it (404).
public partial class PlaybackFiltersTests
{
    [Theory]
    [InlineData("Subtitle", "GetSubtitle", "routeItemId")]
    [InlineData("VideoAttachments", "GetAttachment", "videoId")]
    [InlineData("Trickplay", "GetTrickplayHlsPlaylist", "itemId")]
    [InlineData("MediaSegments", "GetItemSegments", "itemId")]
    public async Task A_media_route_refuses_a_track_stub_without_a_user(string controller, string action, string argument)
    {
        Anonymous();
        var context = Context(controller, action, (argument, _itemId));

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Theory]
    [InlineData("Subtitle", "GetSubtitle", "routeItemId")]
    [InlineData("VideoAttachments", "GetAttachment", "videoId")]
    [InlineData("Trickplay", "GetTrickplayHlsPlaylist", "itemId")]
    [InlineData("MediaSegments", "GetItemSegments", "itemId")]
    public async Task A_media_route_does_not_find_a_track_stub_the_user_cannot_see(string controller, string action, string argument)
    {
        var context = Context(controller, action, (argument, _itemId));

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Theory]
    [InlineData("Subtitle", "GetSubtitle", "routeItemId", true)]
    [InlineData("Subtitle", "GetSubtitle", "routeItemId", false)]
    [InlineData("VideoAttachments", "GetAttachment", "videoId", false)]
    [InlineData("Trickplay", "GetTrickplayHlsPlaylist", "itemId", false)]
    [InlineData("MediaSegments", "GetItemSegments", "itemId", false)]
    public async Task A_media_route_does_not_find_a_visible_track_stub_in_no_library(string controller, string action, string argument, bool known)
    {
        CanSeeItem();
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
        var context = Context(controller, action, (argument, _itemId));

        Assert.False(await RunAsync(GuardFilter(store: TrackStub(known)), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_legacy_audio_segment_that_names_a_track_stub_the_user_cannot_see_is_not_found()
    {
        var context = LegacyAudio(_itemId, "segment0");

        var passed = await RunAsync(GuardFilter(store: TrackStub()), context);

        AssertReadsAsMissing(context, passed, "segmentId");
    }

    [Theory]
    [InlineData("MediaSegments", "GetItemSegments")]
    [InlineData("Audio", "GetAudioStream")]
    [InlineData("Library", "GetFile")]
    public async Task A_track_stub_the_plugin_no_longer_knows_still_wants_a_user(string controller, string action)
    {
        Anonymous();
        var context = Context(controller, action, ("itemId", _itemId));

        Assert.False(await RunAsync(GuardFilter(store: TrackStub(known: false)), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    // Every route of the stub table, on every kind of stub it covers, known to the registry or not (row 514), wants a user
    // who sees the stub, and Download also the download permission (D12). A new entry is checked here with no new test.
    public static TheoryData<int, string, bool> StubRouteCases() => CasesOf(_ => true);

    public static TheoryData<int, string, bool> DownloadRouteCases() => CasesOf(route => route.Access == StubAccess.Download);

    private static TheoryData<int, string, bool> CasesOf(Func<StubRoute, bool> wanted)
    {
        var cases = new TheoryData<int, string, bool>();
        for (var index = 0; index < StockRouteGuardFilter.StubRoutes.Count; index++)
        {
            var route = StockRouteGuardFilter.StubRoutes[index];
            if (!wanted(route))
            {
                continue;
            }

            foreach (var kind in route.Covers == StubCover.Audio ? new[] { "track" } : new[] { "video", "track", "book" })
            {
                cases.Add(index, kind, true);
                cases.Add(index, kind, false);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(StubRouteCases))]
    public async Task A_stub_route_wants_a_user(int route, string kind, bool known)
    {
        Anonymous();
        var context = StubRouteRequest(route);

        Assert.False(await RunAsync(GuardFilter(store: StoreOf(kind, known)), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Opened);
    }

    [Theory]
    [MemberData(nameof(StubRouteCases))]
    public async Task A_stub_route_does_not_find_a_stub_the_user_cannot_see(int route, string kind, bool known)
    {
        MayDownload(true);
        var context = StubRouteRequest(route);

        Assert.False(await RunAsync(GuardFilter(store: StoreOf(kind, known)), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Opened);
    }

    [Theory]
    [MemberData(nameof(StubRouteCases))]
    public async Task A_stub_route_does_not_find_a_visible_stub_in_no_library(int route, string kind, bool known)
    {
        CanSeeItem();
        MayDownload(true);
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
        var context = StubRouteRequest(route);

        Assert.False(await RunAsync(GuardFilter(store: StoreOf(kind, known)), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Opened);
    }

    [Theory]
    [MemberData(nameof(DownloadRouteCases))]
    public async Task A_download_route_wants_the_download_permission(int route, string kind, bool known)
    {
        CanSeeItem();
        MayDownload(false);
        RemoteServes();
        var context = StubRouteRequest(route);

        Assert.False(await RunAsync(GuardFilter(store: StoreOf(kind, known)), context));
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Opened);
    }

    public static TheoryData<int, string> LocalStrmCases()
    {
        var cases = new TheoryData<int, string>();
        for (var index = 0; index < StockRouteGuardFilter.StubRoutes.Count; index++)
        {
            cases.Add(index, "/media/musica/A.strm");
            cases.Add(index, "/federata/S/L/Musica/A.strm");
        }

        return cases;
    }

    // Row 522: with no federated folder set nothing is in it, also a track left by a folder that was removed.
    [Theory]
    [MemberData(nameof(LocalStrmCases))]
    public async Task A_stub_route_leaves_a_strm_to_stock_with_no_folder_set(int route, string path)
    {
        _library.Setup(library => library.GetItemById(_itemId)).Returns(new Audio { Id = _itemId, Path = path });
        Assert.True(await RunAsync(GuardFilter(store: new InMemoryConfigurationStore()), StubRouteRequest(route)));

        _library.Setup(library => library.GetItemById(_itemId)).Returns(new Movie { Id = _itemId, Path = path, ShortcutPath = "http://media.example/a.mkv" });
        Assert.True(await RunAsync(GuardFilter(store: new InMemoryConfigurationStore()), StubRouteRequest(route)));
    }

    [Fact]
    public async Task PlaybackInfo_of_a_local_track_strm_stays_stock_with_no_folder_set()
    {
        Anonymous();
        _library.Setup(library => library.GetItemById(_itemId)).Returns(new Audio { Id = _itemId, Path = "/media/musica/A.strm" });

        Assert.True(await RunAsync(InfoFilter(new InMemoryConfigurationStore()), Context("MediaInfo", "GetPlaybackInfo", ("itemId", _itemId))));
    }

    [Fact]
    public async Task PlaybackInfo_of_a_local_strm_with_versions_stays_stock_with_no_folder_set()
    {
        var local = new Movie { Id = _localId, Path = "/media/film/A.strm", ShortcutPath = "http://media.example/a.mkv", LinkedAlternateVersions = [new LinkedChild { ItemId = _itemId }] };
        _library.Setup(library => library.GetItemById(_localId)).Returns(local);
        _library.Setup(library => library.GetItemById<BaseItem>(_localId, _user)).Returns(local);

        Assert.True(await RunAsync(InfoFilter(new InMemoryConfigurationStore()), Context("MediaInfo", "GetPlaybackInfo", ("itemId", _localId))));
    }

    public static TheoryData<string, string> UnknownStubInfoCases() => new()
    {
        { "video", "GetPlaybackInfo" },
        { "video", "GetPostedPlaybackInfo" },
        { "track", "GetPlaybackInfo" },
        { "track", "GetPostedPlaybackInfo" },
        { "book", "GetPlaybackInfo" },
        { "book", "GetPostedPlaybackInfo" }
    };

    // Row 514: PlaybackInfo of a stub the registry no longer knows wants a user who reaches it, then it is stock's.
    [Theory]
    [MemberData(nameof(UnknownStubInfoCases))]
    public async Task PlaybackInfo_of_an_unknown_stub_wants_a_user(string kind, string action)
    {
        Anonymous();
        var context = Context("MediaInfo", action, ("itemId", _itemId));

        Assert.False(await RunAsync(InfoFilter(StoreOf(kind, known: false)), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Theory]
    [MemberData(nameof(UnknownStubInfoCases))]
    public async Task PlaybackInfo_does_not_find_an_unknown_stub_the_user_cannot_see(string kind, string action)
    {
        var context = Context("MediaInfo", action, ("itemId", _itemId));

        Assert.False(await RunAsync(InfoFilter(StoreOf(kind, known: false)), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Theory]
    [MemberData(nameof(UnknownStubInfoCases))]
    public async Task PlaybackInfo_does_not_find_a_visible_unknown_stub_in_no_library(string kind, string action)
    {
        CanSeeItem();
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
        var context = Context("MediaInfo", action, ("itemId", _itemId));

        Assert.False(await RunAsync(InfoFilter(StoreOf(kind, known: false)), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Theory]
    [MemberData(nameof(UnknownStubInfoCases))]
    public async Task PlaybackInfo_of_an_unknown_stub_in_a_library_stays_stock(string kind, string action)
    {
        CanSeeItem();
        var context = Context("MediaInfo", action, ("itemId", _itemId));

        Assert.True(await RunAsync(InfoFilter(StoreOf(kind, known: false)), context));
        Assert.Null(context.Result);
        Assert.Empty(_fixture.Client.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlaybackInfo_does_not_find_an_unknown_stub_asked_as_the_version_of_a_local_film(bool inBody)
    {
        var film = new Movie { Id = _localId, Path = "/media/film/A.mkv" };
        _library.Setup(library => library.GetItemById(_localId)).Returns(film);
        _library.Setup(library => library.GetItemById<BaseItem>(_localId, _user)).Returns(film);
        var version = _itemId.ToString("N");
        var context = inBody
            ? Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _localId), ("playbackInfoDto", new { MediaSourceId = version }))
            : Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _localId), ("mediaSourceId", version));

        Assert.False(await RunAsync(InfoFilter(StoreOf("video", known: false)), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task PlaybackInfo_does_not_find_a_track_stub_in_no_library_asked_as_the_version_of_a_local_track()
    {
        CanSeeItem();
        var store = TrackStub();
        _library.Setup(library => library.GetItemById(_localId)).Returns(new Audio { Id = _localId, Path = "/media/musica/A.flac" });
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
        var context = Context("MediaInfo", "GetPlaybackInfo", ("itemId", _localId), ("mediaSourceId", _itemId.ToString("N")));

        Assert.False(await RunAsync(InfoFilter(store), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/federata")]
    public async Task PlaybackInfo_of_a_local_video_strm_stays_stock(string folder)
    {
        Anonymous();
        var store = new InMemoryConfigurationStore();
        store.Configuration.FederatedFolderPath = folder;
        _library.Setup(library => library.GetItemById(_itemId)).Returns(new Movie { Path = "/media/film/A.strm", ShortcutPath = "http://media.example/a.mkv" });
        var context = Context("MediaInfo", "GetPlaybackInfo", ("itemId", _itemId));

        Assert.True(await RunAsync(InfoFilter(store), context));
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task PlaybackInfo_of_a_local_video_strm_in_a_library_stays_stock_while_the_folder_is_not_known()
    {
        CanSeeItem();
        var store = new InMemoryConfigurationStore { Unavailable = true };
        _library.Setup(library => library.GetItemById(_itemId)).Returns(new Movie { Path = "/media/film/A.strm", ShortcutPath = "http://media.example/a.mkv" });
        var context = Context("MediaInfo", "GetPlaybackInfo", ("itemId", _itemId));

        Assert.True(await RunAsync(InfoFilter(store), context));
        Assert.Null(context.Result);
    }

    private ActionExecutingContext StubRouteRequest(int route)
    {
        var entry = StockRouteGuardFilter.StubRoutes[route];
        var context = Context(entry.Controller, entry.Action ?? "AnyAction", ("itemId", _itemId));
        context.HttpContext.Response.Body = new MemoryStream();
        return context;
    }

    private InMemoryConfigurationStore StoreOf(string kind, bool known) => kind switch
    {
        "track" => TrackStub(known),
        "book" => BookPlaceholder(known),
        _ => VideoStub(known)
    };

    private InMemoryConfigurationStore VideoStub(bool known)
    {
        _stubs.Setup(stubs => stubs.Find("stub")).Returns(known ? _stub : null);
        return new InMemoryConfigurationStore();
    }
}
