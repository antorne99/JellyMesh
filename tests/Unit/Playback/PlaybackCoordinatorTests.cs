using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

public class PlaybackCoordinatorTests
{
    [Fact]
    public async Task A_remote_that_serves_answers_with_a_session_after_the_first_segment()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(source));

        Assert.Equal(PlaybackOutcomeKind.Remote, outcome.Kind);
        var session = Assert.Single(fixture.Sessions.All);
        Assert.Equal(session.Id, outcome.Response!.PlaySessionId);
        Assert.Contains(fixture.Client.Opened, path => path.Contains("hls1/main/0.mp4", StringComparison.Ordinal));
        Assert.Empty(fixture.Client.Stopped);
    }

    [Fact]
    public async Task Offline_winner_falls_to_the_backup()
    {
        var fixture = new CoordinatorFixture();
        var winner = PlaybackValues.Source("winner");
        var backup = PlaybackValues.Source("backup");
        fixture.Registry.Offline.Add(winner.ServerId);

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(winner, backup));

        Assert.Equal(PlaybackOutcomeKind.Remote, outcome.Kind);
        Assert.Equal(backup, Assert.Single(fixture.Sessions.All).Source);
    }

    [Fact]
    public async Task A_failing_first_segment_closes_that_transcode_and_tries_the_next_source()
    {
        var fixture = new CoordinatorFixture();
        var winner = PlaybackValues.Source("winner");
        var backup = PlaybackValues.Source("backup");
        fixture.Client.Open = (request, _) => Task.FromResult(
            request.Path.Contains("hls1", StringComparison.Ordinal) && fixture.Client.Requests.Count == 1
                ? RemoteResult.Fail<RemoteContent>(RemoteFailure.ServerError)
                : FakePlaybackClient.Bytes());

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(winner, backup));

        Assert.Equal(PlaybackOutcomeKind.Remote, outcome.Kind);
        Assert.Equal(backup, Assert.Single(fixture.Sessions.All).Source);
        Assert.Equal(PlaybackValues.RemotePlaySession + "winner", Assert.Single(fixture.Client.Stopped));
    }

    [Fact]
    public async Task No_transcoding_url_and_no_direct_play_means_fallback()
    {
        var fixture = new CoordinatorFixture();
        fixture.Client.Answer = (request, _) =>
        {
            var answer = FakePlaybackClient.Hls(request);
            answer.Value!.MediaSources[0].TranscodingUrl = null;
            return Task.FromResult(answer);
        };

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));

        Assert.Equal(PlaybackOutcomeKind.LocalTranscode, outcome.Kind);
        Assert.Empty(fixture.Sessions.All);
    }

    [Fact]
    public async Task Master_codecs_outside_the_profile_mean_fallback()
    {
        var fixture = new CoordinatorFixture();
        fixture.Client.Master = PlaybackValues.MasterPlaylist.Replace("vp09.00.10.08", "hvc1.2.4.L153.B0", StringComparison.Ordinal);

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()), PlaybackValues.WebProfile);

        Assert.Equal(PlaybackOutcomeKind.LocalTranscode, outcome.Kind);
        Assert.Single(fixture.Client.Stopped);
    }

    [Fact]
    public async Task A_policy_without_video_transcoding_rules_the_remote_out_before_anything_starts()
    {
        var fixture = new CoordinatorFixture();
        fixture.Client.Policy = new RemoteTranscodePolicy("none", false, true, true);
        fixture.Client.Answer = (request, _) =>
        {
            var answer = FakePlaybackClient.Hls(request);
            answer.Value!.MediaSources[0].TranscodingUrl += ",VideoCodecNotSupported";
            return Task.FromResult(answer);
        };

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));

        Assert.Equal(PlaybackOutcomeKind.LocalTranscode, outcome.Kind);
        Assert.DoesNotContain(fixture.Client.Opened, path => path.Contains("hls1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task All_offline_means_unavailable()
    {
        var fixture = new CoordinatorFixture();
        var first = PlaybackValues.Source("first");
        var second = PlaybackValues.Source("second");
        fixture.Registry.Offline.Add(first.ServerId);
        fixture.Registry.Offline.Add(second.ServerId);

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(first, second));

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
    }

    [Fact]
    public async Task A_missing_key_suspends_remote_playback_without_a_local_transcode()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();
        fixture.Registry.Offline.Add(source.ServerId);
        fixture.Registry.OfflineFailure = RemoteFailure.Suspended;

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(source));

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
        Assert.Empty(fixture.Client.Requests);
    }

    [Fact]
    public async Task Reachable_remote_without_the_file_means_unavailable()
    {
        var fixture = new CoordinatorFixture();
        fixture.Client.Answer = (_, _) => Task.FromResult(RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.ServerError));
        fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.Fail<RemoteContent>(RemoteFailure.ServerError));

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
    }

    [Fact]
    public async Task Direct_play_gets_a_session_with_no_transcode_to_close()
    {
        var fixture = new CoordinatorFixture();
        fixture.Client.Answer = (request, _) => Task.FromResult(FakePlaybackClient.Direct(request));

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));

        var session = Assert.Single(fixture.Sessions.All);
        Assert.Equal(PlaybackStreamKind.Direct, session.Kind);
        var source = Assert.Single(outcome.Response!.MediaSources);
        Assert.False(source.SupportsDirectPlay);
        Assert.True(source.SupportsTranscoding);
        await fixture.Coordinator.CloseAsync(session.Id);
        Assert.Empty(fixture.Client.Stopped);
    }

    [Fact]
    public async Task A_remote_timeout_is_offline_and_bounded()
    {
        var fixture = new CoordinatorFixture();
        fixture.Client.Answer = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        };

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()))
            .WaitAsync(PlaybackTimeouts.RemotePlaybackInfo + TimeSpan.FromSeconds(10));

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
    }

    [Fact]
    public async Task The_limit_on_a_remote_comes_from_the_settings()
    {
        var fixture = new CoordinatorFixture();
        fixture.Store.Configuration.RemotePlaybackInfoTimeoutSeconds = 0;
        fixture.Client.Answer = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        };

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source())).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
        Assert.False(outcome.AnyReachable);
    }

    [Fact]
    public async Task The_limit_on_opening_a_stream_comes_from_the_settings()
    {
        var fixture = new CoordinatorFixture();
        fixture.Store.Configuration.StreamOpenTimeoutSeconds = 0;
        fixture.Client.Open = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        };
        var stream = new RemoteStreamRequest(HttpMethod.Get, "/x", null);

        var opened = await fixture.Coordinator.OpenAsync(Guid.NewGuid(), stream, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RemoteFailure.Timeout, opened.Failure);
    }

    [Fact]
    public async Task A_new_idle_time_applies_to_a_running_session()
    {
        var fixture = new CoordinatorFixture();
        await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));

        fixture.Clock.Advance(TimeSpan.FromSeconds(45));
        await fixture.Coordinator.SweepAsync();
        Assert.Empty(fixture.Client.Stopped);

        fixture.Store.Configuration.PlaybackIdleSeconds = 45;
        await fixture.Coordinator.SweepAsync();
        Assert.Single(fixture.Client.Stopped);
    }

    [Fact]
    public async Task The_time_a_session_is_kept_comes_from_the_settings()
    {
        var fixture = new CoordinatorFixture();
        fixture.Store.Configuration.SessionRetentionHours = 2;
        await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));

        fixture.Clock.Advance(TimeSpan.FromHours(1));
        await fixture.Coordinator.SweepAsync();
        Assert.NotEmpty(fixture.Sessions.All);

        fixture.Clock.Advance(TimeSpan.FromHours(1));
        await fixture.Coordinator.SweepAsync();
        Assert.Empty(fixture.Sessions.All);
    }

    [Fact]
    public async Task The_time_a_policy_is_kept_comes_from_the_settings()
    {
        var fixture = new CoordinatorFixture();
        fixture.Store.Configuration.PolicyCacheMinutes = 1;
        var stub = PlaybackValues.Stub(PlaybackValues.Source());

        await fixture.OpenAsync(stub);
        fixture.Clock.Advance(TimeSpan.FromSeconds(59));
        await fixture.OpenAsync(stub);
        Assert.Equal(1, fixture.Client.PolicyReads);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.OpenAsync(stub);
        Assert.Equal(2, fixture.Client.PolicyReads);
    }

    [Fact]
    public async Task Every_server_offline_is_the_only_outcome_where_nobody_answered()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();
        fixture.Registry.Offline.Add(source.ServerId);

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(source));

        Assert.False(outcome.AnyReachable);
        Assert.Equal(PlaybackRefusal.Text, PlaybackRefusal.TextOf(outcome));
    }

    [Fact]
    public async Task A_server_that_answers_but_cannot_serve_is_not_called_unreachable()
    {
        var fixture = new CoordinatorFixture();
        fixture.Client.Answer = (_, _) => Task.FromResult(RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.ServerError));
        fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.Fail<RemoteContent>(RemoteFailure.ServerError));

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
        Assert.True(outcome.AnyReachable);
        Assert.Equal(PlaybackRefusal.FailedText, PlaybackRefusal.TextOf(outcome));
    }

    [Fact]
    public async Task A_remote_whose_file_is_readable_is_not_called_unreachable()
    {
        var fixture = new CoordinatorFixture();
        fixture.Client.Answer = (_, _) => Task.FromResult(RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.ServerError));

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));

        Assert.Equal(PlaybackOutcomeKind.LocalTranscode, outcome.Kind);
        Assert.Equal(PlaybackRefusal.FailedText, PlaybackRefusal.TextOf(outcome));
    }

    [Fact]
    public async Task A_new_playback_of_the_same_stream_closes_the_old_one_after_the_new_one_works()
    {
        var fixture = new CoordinatorFixture();
        var stub = PlaybackValues.Stub(PlaybackValues.Source());

        await fixture.OpenAsync(stub);
        var first = Assert.Single(fixture.Sessions.All);
        await fixture.OpenAsync(stub, audio: 3);

        var second = Assert.Single(fixture.Sessions.All);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.RemotePlaySessionId, Assert.Single(fixture.Client.Stopped));
    }

    [Fact]
    public async Task A_stream_change_stays_on_the_source_in_use_and_keeps_its_indexes()
    {
        var fixture = new CoordinatorFixture();
        var winner = PlaybackValues.Source("winner");
        var backup = PlaybackValues.Source("backup");
        var stub = PlaybackValues.Stub(winner, backup);
        fixture.Registry.Offline.Add(winner.ServerId);
        await fixture.OpenAsync(stub);
        fixture.Registry.Offline.Clear();
        fixture.Client.Requests.Clear();

        await fixture.OpenAsync(stub, audio: 3);

        var request = Assert.Single(fixture.Client.Requests);
        Assert.Equal("backup", request.ItemId);
        Assert.Equal(3, request.AudioStreamIndex);
    }

    [Fact]
    public async Task Indexes_are_dropped_on_a_source_whose_numbering_the_client_never_saw()
    {
        var fixture = new CoordinatorFixture();
        var winner = PlaybackValues.Source("winner");
        var backup = PlaybackValues.Source("backup");
        fixture.Registry.Offline.Add(winner.ServerId);

        await fixture.OpenAsync(PlaybackValues.Stub(winner, backup), audio: 3);

        Assert.Null(Assert.Single(fixture.Client.Requests).AudioStreamIndex);
    }

    [Fact]
    public async Task The_request_carries_the_real_profile()
    {
        var fixture = new CoordinatorFixture();

        await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()), PlaybackValues.WebProfile, 2);

        var request = Assert.Single(fixture.Client.Requests);
        Assert.Same(PlaybackValues.WebProfile, request.DeviceProfile);
        Assert.Equal("remote-item-ms", request.MediaSourceId);
    }

    [Fact]
    public async Task Close_makes_the_session_unreachable_before_stopping_the_remote()
    {
        var fixture = new CoordinatorFixture();
        await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));
        var session = Assert.Single(fixture.Sessions.All);
        var foundDuringStop = true;
        fixture.Client.OnStop = () => foundDuringStop = fixture.Sessions.Find(session.Id) is not null;

        await fixture.Coordinator.CloseAsync(session.Id);
        await fixture.Coordinator.CloseAsync(session.Id);

        Assert.False(foundDuringStop);
        Assert.Single(fixture.Client.Stopped);
    }

    [Fact]
    public async Task Sweeper_closes_an_idle_transcode_once_and_keeps_the_session_for_resuming()
    {
        var fixture = new CoordinatorFixture();
        await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));
        var session = Assert.Single(fixture.Sessions.All);

        fixture.Clock.Advance(PlaybackTimeouts.Idle - TimeSpan.FromSeconds(1));
        await fixture.Coordinator.SweepAsync();
        Assert.Empty(fixture.Client.Stopped);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Coordinator.SweepAsync();
        await fixture.Coordinator.SweepAsync();
        Assert.Single(fixture.Client.Stopped);
        Assert.NotNull(fixture.Sessions.Find(session.Id));

        session.BeginRequest(fixture.Clock).Dispose();
        fixture.Clock.Advance(PlaybackTimeouts.Idle);
        await fixture.Coordinator.SweepAsync();
        Assert.Equal(2, fixture.Client.Stopped.Count);
    }

    [Fact]
    public async Task Sweeper_spares_a_session_serving_a_request()
    {
        var fixture = new CoordinatorFixture();
        await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));
        var session = Assert.Single(fixture.Sessions.All);

        using var request = session.BeginRequest(fixture.Clock);
        fixture.Clock.Advance(PlaybackTimeouts.Idle * 2);
        await fixture.Coordinator.SweepAsync();

        Assert.Empty(fixture.Client.Stopped);
    }

    [Fact]
    public async Task Sweeper_forgets_a_session_after_the_retention()
    {
        var fixture = new CoordinatorFixture();
        await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));

        fixture.Clock.Advance(PlaybackTimeouts.SessionRetention);
        await fixture.Coordinator.SweepAsync();

        Assert.Empty(fixture.Sessions.All);
    }

    [Fact]
    public async Task Shutdown_closes_every_session()
    {
        var fixture = new CoordinatorFixture();
        await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));

        await fixture.Coordinator.CloseAllAsync();

        Assert.Empty(fixture.Sessions.All);
        Assert.Single(fixture.Client.Stopped);
    }

    [Fact]
    public async Task The_file_of_a_stub_comes_from_the_first_source_that_opens()
    {
        var fixture = new CoordinatorFixture();
        var winner = PlaybackValues.Source("winner");
        var backup = PlaybackValues.Source("backup");
        fixture.Registry.Offline.Add(winner.ServerId);

        using var content = await fixture.Coordinator.OpenFileAsync(PlaybackValues.Stub(winner, backup), System.Net.Http.HttpMethod.Get, "bytes=0-", CancellationToken.None);

        Assert.NotNull(content);
        Assert.Contains("/Videos/backup/stream?static=true", Assert.Single(fixture.Client.Opened), StringComparison.Ordinal);
    }

    [Fact]
    public void Subtitles_of_a_local_transcode_go_to_the_remote_of_the_file()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source("file");
        var external = new MediaBrowser.Model.Entities.MediaStream { Index = 1, DeliveryUrl = "/Videos/local/local/Subtitles/1/0/Stream.vtt?ApiKey=" + PlaybackValues.LocalToken };
        var embedded = new MediaBrowser.Model.Entities.MediaStream { Index = 5, DeliveryUrl = "/Videos/local/local/Subtitles/5/0/Stream.vtt?ApiKey=" + PlaybackValues.LocalToken };

        var session = fixture.Coordinator.MapLocalSubtitle(PlaybackValues.Identity(), source, external, null, PlaybackValues.LocalToken);
        var same = fixture.Coordinator.MapLocalSubtitle(PlaybackValues.Identity(), source, embedded, session, PlaybackValues.LocalToken);

        Assert.Same(session, same);
        Assert.Equal(PlaybackStreamKind.Subtitles, Assert.Single(fixture.Sessions.All).Kind);
        Assert.Equal("/Videos/file/file-ms/Subtitles/1/0/Stream.vtt", session.FindUpstream(0, "Stream.vtt", []));
        Assert.Equal("/Videos/file/file-ms/Subtitles/5/0/Stream.vtt", session.FindUpstream(1, "Stream.vtt", []));
        Assert.StartsWith("/JellyMesh/Playback/" + session.Id + "/f/1/Stream.vtt?ApiKey=", embedded.DeliveryUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_source_for_the_file_means_nothing_to_serve()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();
        fixture.Registry.Offline.Add(source.ServerId);

        var content = await fixture.Coordinator.OpenFileAsync(PlaybackValues.Stub(source), System.Net.Http.HttpMethod.Get, null, CancellationToken.None);

        Assert.Null(content);
    }
}
