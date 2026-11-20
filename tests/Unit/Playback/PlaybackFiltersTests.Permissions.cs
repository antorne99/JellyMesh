using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Playback;
using MediaBrowser.Controller.Entities;
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

    // Every route of the stub table, on every kind of stub it covers, wants a user who sees the stub, and Download also
    // the download permission (D12). A route added to the table is checked here with no new test.
    public static TheoryData<int, string> StubRouteCases() => CasesOf(_ => true);

    public static TheoryData<int, string> DownloadRouteCases() => CasesOf(route => route.Access == StubAccess.Download);

    private static TheoryData<int, string> CasesOf(Func<StubRoute, bool> wanted)
    {
        var cases = new TheoryData<int, string>();
        for (var index = 0; index < StockRouteGuardFilter.StubRoutes.Count; index++)
        {
            var route = StockRouteGuardFilter.StubRoutes[index];
            if (!wanted(route))
            {
                continue;
            }

            foreach (var kind in route.Covers == StubCover.Audio ? new[] { "track" } : new[] { "video", "track", "book" })
            {
                cases.Add(index, kind);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(StubRouteCases))]
    public async Task A_stub_route_wants_a_user(int route, string kind)
    {
        Anonymous();
        var context = StubRouteRequest(route);

        Assert.False(await RunAsync(GuardFilter(store: StoreOf(kind)), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Opened);
    }

    [Theory]
    [MemberData(nameof(StubRouteCases))]
    public async Task A_stub_route_does_not_find_a_stub_the_user_cannot_see(int route, string kind)
    {
        MayDownload(true);
        var context = StubRouteRequest(route);

        Assert.False(await RunAsync(GuardFilter(store: StoreOf(kind)), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Opened);
    }

    [Theory]
    [MemberData(nameof(StubRouteCases))]
    public async Task A_stub_route_does_not_find_a_visible_stub_in_no_library(int route, string kind)
    {
        CanSeeItem();
        MayDownload(true);
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
        var context = StubRouteRequest(route);

        Assert.False(await RunAsync(GuardFilter(store: StoreOf(kind)), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Opened);
    }

    [Theory]
    [MemberData(nameof(DownloadRouteCases))]
    public async Task A_download_route_wants_the_download_permission(int route, string kind)
    {
        CanSeeItem();
        MayDownload(false);
        RemoteServes();
        var context = StubRouteRequest(route);

        Assert.False(await RunAsync(GuardFilter(store: StoreOf(kind)), context));
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Opened);
    }

    private ActionExecutingContext StubRouteRequest(int route)
    {
        var entry = StockRouteGuardFilter.StubRoutes[route];
        var context = Context(entry.Controller, entry.Action ?? "AnyAction", ("itemId", _itemId));
        context.HttpContext.Response.Body = new MemoryStream();
        return context;
    }

    private InMemoryConfigurationStore StoreOf(string kind) => kind switch
    {
        "track" => TrackStub(),
        "book" => BookPlaceholder(),
        _ => new InMemoryConfigurationStore()
    };
}
