using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.MediaInfo;
using Moq;
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

    private static PlaybackOpenRequest Request(StubEntry stub) => new()
    {
        Identity = PlaybackValues.Identity(),
        Stub = stub,
        LocalToken = PlaybackValues.LocalToken
    };

    [Fact]
    public async Task A_failed_first_segment_gives_the_slot_back_before_the_next_source()
    {
        var fixture = new CoordinatorFixture();
        var winner = PlaybackValues.Source("winner");
        var backup = PlaybackValues.Source("backup");
        fixture.Registry.AddJellyMesh(winner.ServerId);
        var releasedBeforeBackup = false;
        fixture.Client.Answer = (request, _) =>
        {
            releasedBeforeBackup = fixture.Client.Released.Count == 1;
            return Task.FromResult(FakePlaybackClient.Hls(request));
        };
        fixture.Client.Open = (request, _) => Task.FromResult(
            request.Path.Contains("hls1", StringComparison.Ordinal) && fixture.Client.Requests.Count == 1
                ? RemoteResult.Fail<RemoteContent>(RemoteFailure.ServerError)
                : FakePlaybackClient.Bytes());

        await fixture.OpenAsync(PlaybackValues.Stub(winner, backup));

        Assert.True(releasedBeforeBackup);
        Assert.Equal(PlaybackValues.RemotePlaySession + "winner", Assert.Single(fixture.Client.Released));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_client_that_leaves_during_the_probe_gives_the_slot_back(bool hls)
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();
        fixture.Registry.AddJellyMesh(source.ServerId);
        using var leaving = new CancellationTokenSource();
        fixture.Client.Answer = (request, _) => Task.FromResult(hls ? FakePlaybackClient.Hls(request) : FakePlaybackClient.Direct(request));
        fixture.Client.Open = async (_, token) =>
        {
            await leaving.CancelAsync();
            token.ThrowIfCancellationRequested();
            return FakePlaybackClient.Bytes();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Coordinator.OpenAsync(Request(PlaybackValues.Stub(source)), leaving.Token));

        Assert.Single(fixture.Client.Released);
        Assert.Empty(fixture.Sessions.All);
    }

    [Fact]
    public async Task A_client_that_leaves_right_after_a_session_opened_closes_it()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();
        fixture.Registry.AddJellyMesh(source.ServerId);
        using var leaving = new CancellationTokenSource();
        var opened = 0;
        fixture.Client.Open = async (_, _) =>
        {
            // The last probe of the session: it succeeds, and the client goes before the answer.
            if (++opened == 1)
            {
                await leaving.CancelAsync();
            }

            return FakePlaybackClient.Bytes();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Coordinator.OpenAsync(Request(PlaybackValues.Stub(source)), leaving.Token));

        Assert.Empty(fixture.Sessions.All);
        Assert.Single(fixture.Client.Stopped);
        Assert.Single(fixture.Client.Released);
    }

    [Fact]
    public async Task An_answer_with_no_usable_source_gives_the_slot_back()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();
        fixture.Registry.AddJellyMesh(source.ServerId);
        fixture.Client.Answer = (_, _) => Task.FromResult(RemoteResult.Success(new PlaybackInfoResponse { PlaySessionId = "held", MediaSources = [] }));

        await fixture.OpenAsync(PlaybackValues.Stub(source));

        Assert.Equal("held", Assert.Single(fixture.Client.Released));
    }

    [Fact]
    public async Task A_policy_that_rules_out_a_remote_gives_the_slot_back()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();
        fixture.Registry.AddJellyMesh(source.ServerId);
        fixture.Client.Policy = new RemoteTranscodePolicy("none", false, true, true);
        fixture.Client.Answer = (request, _) =>
        {
            var answer = FakePlaybackClient.Hls(request);
            answer.Value!.MediaSources[0].TranscodingUrl += ",VideoCodecNotSupported";
            return Task.FromResult(answer);
        };

        await fixture.OpenAsync(PlaybackValues.Stub(source));

        Assert.Single(fixture.Client.Released);
    }

    [Fact]
    public async Task The_sweeper_gives_the_slot_of_an_idle_session_back()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();
        fixture.Registry.AddJellyMesh(source.ServerId);
        await fixture.OpenAsync(PlaybackValues.Stub(source));

        fixture.Clock.Advance(PlaybackTimeouts.Idle);
        await fixture.Coordinator.SweepAsync();

        Assert.Single(fixture.Client.Released);
    }

    [Fact]
    public async Task A_probe_says_so_to_a_jellymesh_remote_only()
    {
        var fixture = new CoordinatorFixture();
        var meshed = PlaybackValues.Source("meshed");
        var stock = PlaybackValues.Source("stock");
        fixture.Registry.AddJellyMesh(meshed.ServerId);
        var seen = new System.Collections.Generic.List<RemoteStreamRequest>();
        fixture.Client.Open = (request, _) =>
        {
            seen.Add(request);
            return Task.FromResult(FakePlaybackClient.Bytes());
        };

        await fixture.OpenAsync(PlaybackValues.Stub(meshed));
        var probes = seen.Count;
        await fixture.OpenAsync(PlaybackValues.Stub(stock));

        Assert.All(seen.Take(probes), request => Assert.True(request.IsProbe));
        Assert.All(seen.Skip(probes), request => Assert.False(request.IsProbe));
    }

    [Fact]
    public async Task A_stopped_report_closes_the_session_of_its_user()
    {
        var fixture = new CoordinatorFixture();
        await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));
        var session = Assert.Single(fixture.Sessions.All);

        await fixture.Coordinator.CloseReportedAsync(session.Id, [session.Identity.UserId]);

        Assert.Empty(fixture.Sessions.All);
        Assert.Single(fixture.Client.Stopped);
    }

    [Fact]
    public async Task A_stopped_report_of_another_user_leaves_the_session()
    {
        var fixture = new CoordinatorFixture();
        await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));
        var session = Assert.Single(fixture.Sessions.All);

        await fixture.Coordinator.CloseReportedAsync(session.Id, [Guid.NewGuid()]);

        Assert.NotNull(fixture.Sessions.Find(session.Id));
        Assert.Empty(fixture.Client.Stopped);
    }

    [Fact]
    public async Task A_file_refused_by_every_source_at_the_limit_explains_a_failure_once()
    {
        var fixture = new CoordinatorFixture();
        var stub = PlaybackValues.Stub(PlaybackValues.Source("one"), PlaybackValues.Source("two"));
        fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.Fail<RemoteContent>(RemoteFailure.StreamLimitReached));

        Assert.Null(await fixture.Coordinator.OpenFileAsync(stub, HttpMethod.Get, null, CancellationToken.None));

        Assert.True(fixture.Coordinator.TakeFileRefusal(stub.Id));
        Assert.False(fixture.Coordinator.TakeFileRefusal(stub.Id));
    }

    [Fact]
    public async Task A_file_with_any_other_failure_says_nothing_of_the_limit()
    {
        var fixture = new CoordinatorFixture();
        var stub = PlaybackValues.Stub(PlaybackValues.Source("one"), PlaybackValues.Source("two"));
        var failures = new Queue<RemoteFailure>([RemoteFailure.StreamLimitReached, RemoteFailure.ServerError]);
        fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.Fail<RemoteContent>(failures.Dequeue()));

        await fixture.Coordinator.OpenFileAsync(stub, HttpMethod.Get, null, CancellationToken.None);

        Assert.False(fixture.Coordinator.TakeFileRefusal(stub.Id));
    }

    [Fact]
    public async Task An_old_refusal_of_a_file_explains_nothing()
    {
        var fixture = new CoordinatorFixture();
        var stub = PlaybackValues.Stub(PlaybackValues.Source());
        fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.Fail<RemoteContent>(RemoteFailure.StreamLimitReached));
        await fixture.Coordinator.OpenFileAsync(stub, HttpMethod.Get, null, CancellationToken.None);

        fixture.Clock.Advance(PlaybackTimeouts.FileRefusalMemory);

        Assert.False(fixture.Coordinator.TakeFileRefusal(stub.Id));
    }

    [Fact]
    public async Task A_server_about_to_go_gives_back_the_slots_of_its_sessions_and_only_those()
    {
        var fixture = new CoordinatorFixture();
        var leaving = PlaybackValues.Source("leaving");
        var staying = PlaybackValues.Source("staying");
        fixture.Registry.AddJellyMesh(leaving.ServerId);
        fixture.Registry.AddJellyMesh(staying.ServerId);
        await fixture.OpenAsync(PlaybackValues.Stub(leaving));
        var other = PlaybackValues.Identity() with { DeviceId = "other" };
        await fixture.Coordinator.OpenAsync(new PlaybackOpenRequest { Identity = other, Stub = PlaybackValues.Stub(staying), LocalToken = PlaybackValues.LocalToken }, CancellationToken.None);

        await fixture.Coordinator.CloseServerAsync(leaving.ServerId);

        Assert.Equal(staying, Assert.Single(fixture.Sessions.All).Source);
        Assert.Equal(PlaybackValues.RemotePlaySession + "leaving", Assert.Single(fixture.Client.Released));
    }

    // Decision 413: removing a server ends what plays from it at once, before anything is asked of the remote.
    [Fact]
    public async Task A_server_about_to_go_ends_its_open_streams_at_once_and_only_those()
    {
        var fixture = new CoordinatorFixture();
        var leaving = PlaybackValues.Source("leaving");
        var staying = PlaybackValues.Source("staying");
        fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.Success(new RemoteContent(System.Net.HttpStatusCode.OK, new RemoteContentHeaders("video/mp4", null, null, null), new HangingStream())));
        using var gone = (await fixture.Coordinator.OpenAsync(leaving.ServerId, new RemoteStreamRequest(HttpMethod.Get, "/Videos/leaving/stream", null), CancellationToken.None)).Value!;
        using var kept = (await fixture.Coordinator.OpenAsync(staying.ServerId, new RemoteStreamRequest(HttpMethod.Get, "/Videos/staying/stream", null), CancellationToken.None)).Value!;
        var goneCopy = gone.CopyToAsync(System.IO.Stream.Null, TimeSpan.FromMinutes(5), CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var keptCopy = kept.CopyToAsync(System.IO.Stream.Null, TimeSpan.FromMinutes(5), stop.Token);

        await fixture.Coordinator.CloseServerAsync(leaving.ServerId);

        await Assert.ThrowsAsync<RemoteStreamClosedException>(() => goneCopy.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(keptCopy.IsCompleted);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => keptCopy.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // A remote that hangs costs the removal one short wait, not one per call.
    [Fact]
    public async Task A_server_about_to_go_stops_and_gives_back_within_one_shared_limit()
    {
        var fixture = new CoordinatorFixture();
        var leaving = PlaybackValues.Source("leaving");
        fixture.Registry.AddJellyMesh(leaving.ServerId);
        await fixture.OpenAsync(PlaybackValues.Stub(leaving));
        fixture.Client.StopsHang = true;
        fixture.Coordinator.RemovalLimit = TimeSpan.FromMilliseconds(300);

        await fixture.Coordinator.CloseServerAsync(leaving.ServerId).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(fixture.Client.Stopped);
        Assert.Empty(fixture.Client.Released);
        Assert.Empty(fixture.Sessions.All);
    }

    [Fact]
    public async Task A_server_about_to_go_opens_nothing_more_until_its_removal_fails()
    {
        var fixture = new CoordinatorFixture();
        var leaving = PlaybackValues.Source("leaving");
        var request = new RemoteStreamRequest(HttpMethod.Get, "/Videos/leaving/stream", null);

        await fixture.Coordinator.CloseServerAsync(leaving.ServerId);

        Assert.Equal(RemoteFailure.UnknownServer, (await fixture.Coordinator.OpenAsync(leaving.ServerId, request, CancellationToken.None)).Failure);
        Assert.Equal(RemoteFailure.UnknownServer, (await fixture.Coordinator.ReadPlaylistAsync(leaving.ServerId, "/master.m3u8", CancellationToken.None)).Failure);
        Assert.NotEqual(PlaybackOutcomeKind.Remote, (await fixture.OpenAsync(PlaybackValues.Stub(leaving))).Kind);
        Assert.Empty(fixture.Client.Opened);
        Assert.Empty(fixture.Client.Requests);

        fixture.Coordinator.ReopenServer(leaving.ServerId);

        using var opened = (await fixture.Coordinator.OpenAsync(leaving.ServerId, request, CancellationToken.None)).Value;
        Assert.NotNull(opened);
        await opened.CopyToAsync(System.IO.Stream.Null, TimeSpan.FromMinutes(5), CancellationToken.None);
    }

    [Fact]
    public async Task A_server_about_to_go_closes_the_local_transcodes_of_its_files_and_only_those()
    {
        var fixture = new CoordinatorFixture();
        var leaving = PlaybackValues.Source("leaving");
        var staying = PlaybackValues.Source("staying");
        var user = Guid.NewGuid();
        fixture.Coordinator.RecordLocalPlaySession(user, Guid.NewGuid(), "local-leaving", ["device"]);
        fixture.Coordinator.RecordLocalTranscode("local-leaving", leaving);
        fixture.Coordinator.RecordLocalPlaySession(user, Guid.NewGuid(), "local-staying", ["device"]);
        fixture.Coordinator.RecordLocalTranscode("local-staying", staying);

        await fixture.Coordinator.CloseServerAsync(leaving.ServerId);

        fixture.Transcodes.Verify(manager => manager.KillTranscodingJobs(string.Empty, "local-leaving", It.IsAny<Func<string, bool>>()), Moq.Times.Once);
        fixture.Transcodes.Verify(manager => manager.KillTranscodingJobs(It.IsAny<string>(), "local-staying", It.IsAny<Func<string, bool>>()), Moq.Times.Never);
    }

    [Fact]
    public async Task A_local_transcode_its_user_ended_is_not_closed_again()
    {
        var fixture = new CoordinatorFixture();
        var leaving = PlaybackValues.Source("leaving");
        var user = Guid.NewGuid();
        fixture.Coordinator.RecordLocalPlaySession(user, Guid.NewGuid(), "local-leaving", ["device"]);
        fixture.Coordinator.RecordLocalTranscode("local-leaving", leaving);
        fixture.Sessions.EndLocal(user, "local-leaving");

        await fixture.Coordinator.CloseServerAsync(leaving.ServerId);

        fixture.Transcodes.Verify(manager => manager.KillTranscodingJobs(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<string, bool>>()), Moq.Times.Never);
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

    private static PlaybackIdentity OtherDevice(int device) => new(PlaybackValues.Identity().UserId, "device-" + device, Guid.NewGuid(), "stub");

    private static PlaybackOpenRequest OpenRequest(PlaybackIdentity identity, StubEntry stub) => new()
    {
        Identity = identity,
        Stub = stub,
        LocalToken = PlaybackValues.LocalToken
    };

    [Theory]
    [InlineData("http://evil.example/master.m3u8")]
    [InlineData("//evil.example/master.m3u8")]
    public async Task A_transcoding_url_of_another_host_is_a_source_that_cannot_serve(string url)
    {
        var fixture = new CoordinatorFixture();
        fixture.Client.Answer = (request, _) =>
        {
            var answer = FakePlaybackClient.Hls(request);
            answer.Value!.MediaSources[0].TranscodingUrl = url;
            return Task.FromResult(answer);
        };
        fixture.Client.Playlists = path => RemoteUrl.TryCombine(new Uri("http://friend/"), path, out _) ? RemoteFailure.None : RemoteFailure.InvalidResponse;

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));

        Assert.NotEqual(PlaybackOutcomeKind.Remote, outcome.Kind);
        Assert.Empty(fixture.Sessions.All);
    }

    [Fact]
    public async Task A_container_name_of_a_remote_never_reaches_a_client_url_as_it_is()
    {
        var fixture = new CoordinatorFixture();
        fixture.Client.Answer = (request, _) =>
        {
            var answer = FakePlaybackClient.Direct(request);
            answer.Value!.MediaSources[0].TranscodingContainer = "mp4?x=http://evil.example/";
            return Task.FromResult(answer);
        };

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()));

        Assert.DoesNotContain("evil", outcome.Response!.MediaSources[0].TranscodingUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", outcome.Response.MediaSources[0].TranscodingContainer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Calls_over_the_limit_of_a_server_wait_and_then_count_as_busy_not_offline()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();
        var held = new TaskCompletionSource();
        var arrived = 0;
        fixture.Client.Open = async (_, token) =>
        {
            Interlocked.Increment(ref arrived);
            await held.Task.WaitAsync(token);
            return FakePlaybackClient.Bytes();
        };
        var request = new RemoteStreamRequest(HttpMethod.Get, "/x.ts", null);

        var calls = Enumerable.Range(0, PlaybackLimits.CallsPerServer)
            .Select(_ => Task.Run(() => fixture.Coordinator.OpenAsync(source.ServerId, request, CancellationToken.None)))
            .ToList();
        while (Volatile.Read(ref arrived) < PlaybackLimits.CallsPerServer)
        {
            await Task.Delay(10);
        }

        var busy = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => fixture.Coordinator.OpenAsync(source.ServerId, request, CancellationToken.None)));

        Assert.Equal(PlaybackLimits.CallsPerServer, Volatile.Read(ref arrived));
        Assert.All(busy, result => Assert.Equal(RemoteFailure.Busy, result.Failure));

        held.SetResult();
        foreach (var result in await Task.WhenAll(calls))
        {
            result.Value?.Dispose();
        }
    }

    [Fact]
    public async Task Stops_and_renewals_do_not_queue_behind_what_clients_opened()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();
        await fixture.OpenAsync(PlaybackValues.Stub(source));
        var session = Assert.Single(fixture.Sessions.All);
        var held = new TaskCompletionSource();
        fixture.Client.Open = async (_, token) =>
        {
            await held.Task.WaitAsync(token);
            return FakePlaybackClient.Bytes();
        };
        var request = new RemoteStreamRequest(HttpMethod.Get, "/x.ts", null);
        var calls = Enumerable.Range(0, PlaybackLimits.CallsPerServer)
            .Select(_ => Task.Run(() => fixture.Coordinator.OpenAsync(source.ServerId, request, CancellationToken.None)))
            .ToList();

        await fixture.Coordinator.CloseAsync(session.Id).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.NotEmpty(fixture.Client.Stopped);
        held.SetResult();
        await Task.WhenAll(calls);
    }

    [Fact]
    public async Task Streams_over_the_limit_of_a_server_are_refused_until_one_ends()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();
        var request = new RemoteStreamRequest(HttpMethod.Get, "/x.ts", null);
        var open = new System.Collections.Generic.List<RemoteContent>();
        for (var stream = 0; stream < PlaybackLimits.StreamsPerServer; stream++)
        {
            var opened = await fixture.Coordinator.OpenAsync(source.ServerId, request, CancellationToken.None);
            open.Add(opened.Value!);
        }

        var refused = await fixture.Coordinator.OpenAsync(source.ServerId, request, CancellationToken.None);
        Assert.Equal(RemoteFailure.Busy, refused.Failure);

        open[0].Dispose();
        open[0].Dispose();

        var next = await fixture.Coordinator.OpenAsync(source.ServerId, request, CancellationToken.None);
        Assert.True(next.IsSuccess);
        var again = await fixture.Coordinator.OpenAsync(source.ServerId, request, CancellationToken.None);
        Assert.Equal(RemoteFailure.Busy, again.Failure);
    }

    [Fact]
    public async Task A_stream_that_fails_to_open_gives_its_place_back()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();
        fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.Fail<RemoteContent>(RemoteFailure.ServerError));
        var request = new RemoteStreamRequest(HttpMethod.Get, "/x.ts", null);

        for (var attempt = 0; attempt < PlaybackLimits.StreamsPerServer * 2; attempt++)
        {
            var result = await fixture.Coordinator.OpenAsync(source.ServerId, request, CancellationToken.None);
            Assert.Equal(RemoteFailure.ServerError, result.Failure);
        }
    }

    [Fact]
    public async Task A_busy_server_is_not_called_offline()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();
        var request = new RemoteStreamRequest(HttpMethod.Get, "/x.ts", null);
        fixture.Client.Answer = (_, _) => Task.FromResult(RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.ServerError));
        for (var stream = 0; stream < PlaybackLimits.StreamsPerServer; stream++)
        {
            await fixture.Coordinator.OpenAsync(source.ServerId, request, CancellationToken.None);
        }

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(source));

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
        Assert.True(outcome.AnyReachable);
        Assert.Equal(PlaybackRefusal.FailedText, PlaybackRefusal.TextOf(outcome));
    }

    [Fact]
    public async Task A_client_inventing_devices_cannot_open_sessions_without_end()
    {
        var fixture = new CoordinatorFixture();
        var stub = PlaybackValues.Stub(PlaybackValues.Source());

        for (var device = 0; device < PlaybackLimits.SessionsPerUser + 5; device++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.Coordinator.OpenAsync(OpenRequest(OtherDevice(device), stub), CancellationToken.None);
        }

        Assert.Equal(PlaybackLimits.SessionsPerUser, fixture.Sessions.All.Count);
        Assert.Equal(5, fixture.Client.Stopped.Count);
        Assert.DoesNotContain(fixture.Sessions.All, session => session.Identity.DeviceId == "device-0");
        Assert.Contains(fixture.Sessions.All, session => session.Identity.DeviceId == "device-" + (PlaybackLimits.SessionsPerUser + 4));
    }

    [Fact]
    public async Task With_every_session_in_use_a_new_playback_is_refused_and_none_is_cut()
    {
        var fixture = new CoordinatorFixture();
        var stub = PlaybackValues.Stub(PlaybackValues.Source());
        var running = new System.Collections.Generic.List<IDisposable>();
        for (var device = 0; device < PlaybackLimits.SessionsPerUser; device++)
        {
            await fixture.Coordinator.OpenAsync(OpenRequest(OtherDevice(device), stub), CancellationToken.None);
        }

        running.AddRange(fixture.Sessions.All.Select(session => session.BeginRequest(fixture.Clock)));

        var outcome = await fixture.Coordinator.OpenAsync(OpenRequest(OtherDevice(99), stub), CancellationToken.None);

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
        Assert.Equal(PlaybackRefusal.FailedText, PlaybackRefusal.TextOf(outcome));
        Assert.Equal(PlaybackLimits.SessionsPerUser, fixture.Sessions.All.Count);
        Assert.Empty(fixture.Client.Stopped);
        running.ForEach(request => request.Dispose());
    }

    [Fact]
    public async Task Sessions_of_other_users_are_not_touched()
    {
        var fixture = new CoordinatorFixture();
        var stub = PlaybackValues.Stub(PlaybackValues.Source());
        var other = new PlaybackIdentity(Guid.NewGuid(), "their-device", Guid.NewGuid(), "stub");
        await fixture.Coordinator.OpenAsync(OpenRequest(other, stub), CancellationToken.None);

        for (var device = 0; device < PlaybackLimits.SessionsPerUser + 1; device++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.Coordinator.OpenAsync(OpenRequest(OtherDevice(device), stub), CancellationToken.None);
        }

        Assert.Contains(fixture.Sessions.All, session => session.Identity == other);
    }

    [Fact]
    public async Task The_stall_limit_is_the_idle_time_of_decision_17()
    {
        var fixture = new CoordinatorFixture();
        fixture.Store.Configuration.PlaybackIdleSeconds = 42;

        Assert.Equal(TimeSpan.FromSeconds(42), fixture.Coordinator.StallLimit);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task What_a_remote_says_about_its_hardware_is_no_line_of_the_log()
    {
        var logger = new CapturingLogger<PlaybackCoordinator>();
        var fixture = new CoordinatorFixture();
        var coordinator = new PlaybackCoordinator(fixture.Registry, fixture.Client, fixture.Sessions, fixture.Store, fixture.LastServed, fixture.Clock, logger);
        fixture.Client.Policy = new RemoteTranscodePolicy("qsv\r\nforged line", true, true, true);
        var stub = PlaybackValues.Stub(PlaybackValues.Source());

        await coordinator.OpenAsync(OpenRequest(PlaybackValues.Identity(), stub), CancellationToken.None);

        Assert.Contains(logger.Lines, line => line.Contains("hardware", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, line => line.Contains("forged", StringComparison.Ordinal));
    }

    private sealed class HangingStream : System.IO.Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
