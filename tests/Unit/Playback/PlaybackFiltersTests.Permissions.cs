using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
}
