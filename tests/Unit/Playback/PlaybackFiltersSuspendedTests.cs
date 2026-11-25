using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

// While the plugin database cannot be used nothing new plays from the remotes, and what plays goes on (row 524).
public partial class PlaybackFiltersTests
{
    private ActionExecutingContext PlaybackInfoRequest()
    {
        CanSeeItem();
        return Context("MediaInfo", "GetPlaybackInfo", ("itemId", _itemId));
    }

    private async Task<ActionExecutingContext> PlayedAsync()
    {
        var context = PlaybackInfoRequest();
        await RunAsync(InfoFilter(), context);
        return context;
    }

    [Fact]
    public async Task With_the_database_damaged_no_playback_opens_and_the_one_running_stays()
    {
        Assert.IsType<OkObjectResult>((await PlayedAsync()).Result);
        var session = Assert.Single(_fixture.Sessions.All);
        var asked = _fixture.Client.Requests.Count;
        _fixture.Damage();

        var context = await PlayedAsync();

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Equal(asked, _fixture.Client.Requests.Count);
        Assert.Same(session, Assert.Single(_fixture.Sessions.All));
        Assert.Empty(_fixture.Client.Stopped);
    }

    // Row 427: the key suspends remote playback with the same outcome, so the viewer reads the same text.
    [Fact]
    public async Task With_the_database_damaged_the_outcome_is_the_one_of_a_suspended_server()
    {
        _fixture.Registry.Offline.Add(_stub.Sources[0].ServerId);
        _fixture.Registry.OfflineFailure = RemoteFailure.Suspended;
        var suspended = await _fixture.OpenAsync(_stub);
        _fixture.Registry.Offline.Clear();
        _fixture.Damage();

        var damaged = await _fixture.OpenAsync(_stub);

        Assert.Equal(suspended, damaged);
        Assert.Equal(PlaybackRefusal.Text, PlaybackRefusal.TextOf(damaged));
    }

    [Fact]
    public async Task With_the_database_damaged_a_local_copy_still_plays()
    {
        CanSeeItem();
        LocalCopyArrived();
        _fixture.Damage();
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId));

        var executed = await RunStockAsync(context, LocalFile(directPlay: true));

        Assert.IsType<OkObjectResult>(executed.Result);
        Assert.Equal(_localId, context.ActionArguments["itemId"]);
        Assert.Empty(_fixture.Client.Requests);
    }

    [Theory]
    [InlineData("GetFile")]
    [InlineData("GetDownload")]
    public async Task With_the_database_damaged_a_file_read_is_refused_and_closes_no_session(string action)
    {
        CanSeeItem();
        MayDownload(true);
        RemoteServes();
        SessionsOf(_user.Id, PlaybackLimits.SessionsPerUser);
        _fixture.Damage();
        var context = FileRequest(action);

        Assert.False(await RunAsync(GuardFilter(), context));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(context));
        Assert.Empty(BodyOf(context));
        Assert.Empty(_fixture.Client.Opened);
        Assert.Equal(PlaybackLimits.SessionsPerUser, _fixture.Sessions.All.Count);
    }

    [Fact]
    public async Task A_file_read_that_runs_when_the_database_is_damaged_goes_on()
    {
        CanSeeItem();
        var held = new TaskCompletionSource();
        var reads = HeldReads(1, held);
        _fixture.Damage();

        held.SetResult();

        Assert.False(Assert.Single(await Task.WhenAll(reads)));
    }

    [Theory]
    [InlineData("Videos", "GetVideoStream")]
    [InlineData("DynamicHls", "GetMasterHlsVideoPlaylist")]
    [InlineData("Audio", "GetAudioStream")]
    [InlineData("UniversalAudio", "GetUniversalAudioStream")]
    public async Task With_the_database_damaged_no_stock_stream_of_a_stub_starts(string controller, string action)
    {
        CanSeeItem();
        _fixture.Damage();
        var context = Context(controller, action, ("itemId", _itemId));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(context));
    }

    // Row 561: lyrics come from a remote, so none are read while the database cannot be used.
    [Fact]
    public async Task With_the_database_damaged_the_lyrics_of_a_track_stub_are_refused_and_no_remote_is_asked()
    {
        CanSeeItem();
        _fixture.Client.Lyrics = _ => RemoteResult.Success(RemoteLyrics.None);
        var store = TrackStub();
        _fixture.Damage();
        var context = LyricsRequest();

        Assert.False(await RunAsync(GuardFilter(store: store), context));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(context));
        Assert.Empty(_fixture.Client.LyricsAsked);
    }

    [Fact]
    public async Task With_the_database_damaged_the_local_transcode_that_runs_goes_on_and_no_other_stub_joins_it()
    {
        CanSeeItem();
        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _itemId);
        _fixture.Coordinator.RecordLocalTranscode("local-play", _stub.Sources[0], "stub");
        var other = Guid.NewGuid();
        _library.Setup(library => library.GetItemById(other)).Returns(new Movie { ShortcutPath = PlaybackRoutes.StrmUrl(8096, null, "other", "signature") });
        _library.Setup(library => library.GetItemById<BaseItem>(other, _user)).Returns(new Movie());
        _stubs.Setup(stubs => stubs.Find("other")).Returns(_stub);
        _fixture.Damage();

        Assert.True(await RunAsync(GuardFilter(), Context("DynamicHls", "GetHlsVideoSegment", ("itemId", _itemId), ("playSessionId", "local-play"))));
        var joined = Context("Videos", "GetVideoStream", ("itemId", other), ("playSessionId", "local-play"));
        Assert.False(await RunAsync(GuardFilter(), joined));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(joined));
    }

    [Fact]
    public async Task With_the_database_damaged_a_local_item_and_the_subtitles_of_a_stub_stay_with_stock()
    {
        CanSeeItem();
        _fixture.Damage();

        Assert.True(await RunAsync(GuardFilter(), Context("Videos", "GetVideoStream", ("itemId", LocalItem()))));
        Assert.True(await RunAsync(GuardFilter(), Context("Subtitle", "GetSubtitle", ("routeItemId", _itemId))));
    }

    [Fact]
    public async Task With_the_database_damaged_the_server_reads_only_the_file_of_a_local_transcode_that_runs()
    {
        _fixture.Damage();
        Assert.Null(await _fixture.Coordinator.OpenFileAsync(_stub, HttpMethod.Get, "bytes=0-", CancellationToken.None));
        Assert.Empty(_fixture.Client.Opened);

        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _itemId);
        _fixture.Coordinator.RecordLocalTranscode("local-play", _stub.Sources[0], _stub.Id);
        using var running = await _fixture.Coordinator.OpenFileAsync(_stub, HttpMethod.Get, "bytes=9-", CancellationToken.None);
        Assert.NotNull(running);
        Assert.Null(await _fixture.Coordinator.OpenFileAsync(_stub with { Id = "other" }, HttpMethod.Get, null, CancellationToken.None));

        _fixture.Sessions.EndLocal(_user.Id, "local-play");
        Assert.Null(await _fixture.Coordinator.OpenFileAsync(_stub, HttpMethod.Get, "bytes=9-", CancellationToken.None));
    }

    [Fact]
    public async Task A_local_transcode_is_recorded_with_the_stub_it_reads()
    {
        CanSeeItem();
        _fixture.Client.Answer = (_, _) => Task.FromResult(RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.ServerError));
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId), ("playbackInfoDto", new { DeviceProfile = new DeviceProfile() }));
        await InfoFilter().OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, [], new object())
        {
            Result = new OkObjectResult(new PlaybackInfoResponse
            {
                PlaySessionId = "local-play",
                MediaSources = [new MediaSourceInfo { Id = _itemId.ToString("N"), TranscodingUrl = "/videos/x/master.m3u8?DeviceId=device" }]
            })
        }));

        Assert.True(_fixture.Sessions.IsReadLocally("stub"));
        Assert.False(_fixture.Sessions.IsReadLocally("other"));
    }

    [Fact]
    public async Task With_the_database_damaged_a_stop_still_closes_the_session_of_its_user()
    {
        var session = new PlaybackSession(PlaybackValues.Identity() with { UserId = _user.Id }, _stub.Sources[0], "remote-ps", PlaybackStreamKind.Hls, "/m", _fixture.Clock.GetUtcNow());
        _fixture.Sessions.Add(session);
        _fixture.Damage();

        Assert.True(await RunAsync(GuardFilter(), Context("HlsSegment", "StopEncodingProcess", ("playSessionId", session.Id))));

        Assert.Empty(_fixture.Sessions.All);
        Assert.Equal("remote-ps", Assert.Single(_fixture.Client.Stopped));
    }

    [Fact]
    public async Task With_the_database_damaged_the_streams_of_a_session_still_open_and_stop_reports_close_it()
    {
        var session = new PlaybackSession(PlaybackValues.Identity() with { UserId = _user.Id }, _stub.Sources[0], "remote-ps", PlaybackStreamKind.Hls, "/m", _fixture.Clock.GetUtcNow());
        _fixture.Sessions.Add(session);
        _fixture.Damage();

        var segment = await _fixture.Coordinator.OpenAsync(session.Source.ServerId, new RemoteStreamRequest(HttpMethod.Get, "/videos/x/0.ts", null), CancellationToken.None);
        var playlist = await _fixture.Coordinator.ReadPlaylistAsync(session, "/videos/x/main.m3u8", CancellationToken.None);
        await _fixture.Coordinator.CloseReportedAsync(session.Id, [_user.Id]);

        Assert.True(segment.IsSuccess);
        segment.Value!.Dispose();
        Assert.True(playlist.IsSuccess);
        Assert.Empty(_fixture.Sessions.All);
        Assert.Equal("remote-ps", Assert.Single(_fixture.Client.Stopped));
    }
}
