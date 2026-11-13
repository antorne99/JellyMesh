using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

// A source that drops during playback goes last for that user and title, and a title that keeps failing is refused (row 57).
public class ReconnectionTests
{
    private static readonly Guid _user = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _otherUser = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid _serverA = Guid.Parse("a0000000-0000-0000-0000-000000000000");
    private static readonly Guid _serverB = Guid.Parse("b0000000-0000-0000-0000-000000000000");

    private readonly CoordinatorFixture _fixture = new();
    private readonly StubEntry _a = ServerStub("a", _serverA, "a1", "a2");
    private readonly StubEntry _b = ServerStub("b", _serverB, "b1");
    private readonly StubEntry _single = ServerStub("a", _serverA, "a1");

    private static StubEntry ServerStub(string id, Guid server, params string[] items) => new()
    {
        Id = id,
        ServerId = server,
        Kind = StubKind.Movie,
        RelativePath = server.ToString("D") + "/Film/x/x - " + id + ".strm",
        Sources = items.Select(item => new StubSource(server, item, item + "-ms")).ToList(),
        IdentityKey = "key"
    };

    private static Guid ItemOf(StubEntry stub) => Guid.Parse("70000000-0000-0000-0000-0000000000" + (stub.Id == "a" ? "0a" : "0b"));

    private static PlaybackIdentity IdentityOf(Guid user, StubEntry stub) => new(user, "device", ItemOf(stub), stub.Id);

    private async Task<PlaybackOutcome> OpenAsync(StubEntry stub, Guid user, params StubEntry[] fallbacks)
    {
        _fixture.Client.Requests.Clear();
        return await _fixture.Coordinator.OpenAsync(
            new PlaybackOpenRequest
            {
                Identity = IdentityOf(user, stub),
                Stub = stub,
                Fallbacks = fallbacks.Select(fallback => new PlaybackCandidate(ItemOf(fallback), fallback)).ToList(),
                LocalToken = PlaybackValues.LocalToken
            },
            CancellationToken.None);
    }

    // Plays the title, then the session that served drops.
    private async Task<PlaybackSession> PlayAndDropAsync(StubEntry stub, Guid user, params StubEntry[] fallbacks)
    {
        var outcome = await OpenAsync(stub, user, fallbacks);
        var session = _fixture.Sessions.Find(outcome.Response!.PlaySessionId)!;
        _fixture.Coordinator.ReportFailure(session);
        return session;
    }

    [Fact]
    public async Task The_next_open_tries_another_source_of_the_stub_before_the_one_that_dropped()
    {
        await PlayAndDropAsync(_a, _user);

        await OpenAsync(_a, _user);

        Assert.Equal("a2", Assert.Single(_fixture.Sessions.All).Source.ItemId);
        Assert.Equal(["a2"], _fixture.Client.Requests.Select(request => request.ItemId));
    }

    [Fact]
    public async Task The_next_open_tries_another_version_before_the_one_that_dropped()
    {
        await PlayAndDropAsync(_single, _user, _b);

        var outcome = await OpenAsync(_single, _user, _b);

        Assert.Equal(IdentityOf(_user, _b), outcome.Served);
        Assert.Equal(["b1"], _fixture.Client.Requests.Select(request => request.ItemId));
    }

    [Fact]
    public async Task The_source_that_dropped_still_serves_when_nothing_else_can()
    {
        await PlayAndDropAsync(_single, _user, _b);
        _fixture.Registry.Offline.Add(_serverB);

        var outcome = await OpenAsync(_single, _user, _b);

        Assert.Equal(IdentityOf(_user, _single), outcome.Served);
        Assert.Equal("a1", Assert.Single(_fixture.Sessions.All).Source.ItemId);
    }

    [Fact]
    public async Task The_source_that_dropped_gets_its_place_back_after_five_minutes()
    {
        await PlayAndDropAsync(_single, _user, _b);

        _fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var outcome = await OpenAsync(_single, _user, _b);

        Assert.Equal(IdentityOf(_user, _single), outcome.Served);
        Assert.Equal(["a1"], _fixture.Client.Requests.Select(request => request.ItemId));
    }

    [Fact]
    public async Task A_drop_for_one_user_leaves_the_order_of_another_user()
    {
        await PlayAndDropAsync(_single, _user, _b);

        var outcome = await OpenAsync(_single, _otherUser, _b);

        Assert.Equal(IdentityOf(_otherUser, _single), outcome.Served);
    }

    [Fact]
    public async Task The_file_no_longer_opens_first_the_source_that_dropped()
    {
        _fixture.Client.Answer = (request, _) => Task.FromResult(request.ItemId == "a1"
            ? RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.ServerError)
            : FakePlaybackClient.Hls(request));
        await PlayAndDropAsync(_a, _user);
        _fixture.Client.Opened.Clear();

        using var content = await _fixture.Coordinator.OpenFileAsync(_a, HttpMethod.Get, null, CancellationToken.None);

        Assert.Contains("/Videos/a1/stream?static=true", _fixture.Client.Opened.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_third_reconnection_still_plays()
    {
        for (var drop = 0; drop < PlaybackLimits.Reconnections; drop++)
        {
            await PlayAndDropAsync(_single, _user, _b);
        }

        var outcome = await OpenAsync(_single, _user, _b);

        Assert.Equal(PlaybackOutcomeKind.Remote, outcome.Kind);
    }

    [Fact]
    public async Task The_fourth_reconnection_is_refused_without_asking_a_remote_and_closes_the_session_that_dropped()
    {
        PlaybackSession? last = null;
        for (var drop = 0; drop <= PlaybackLimits.Reconnections; drop++)
        {
            last = await PlayAndDropAsync(_single, _user, _b);
        }

        var outcome = await OpenAsync(_single, _user, _b);

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
        Assert.True(outcome.AnyReachable);
        Assert.False(outcome.AtStreamLimit);
        Assert.Empty(_fixture.Client.Requests);
        Assert.Contains(last!.RemotePlaySessionId, _fixture.Client.Stopped);
        Assert.DoesNotContain(last, _fixture.Sessions.All);
    }

    [Fact]
    public async Task The_retries_of_a_player_on_one_session_are_one_drop()
    {
        var session = await PlayAndDropAsync(_single, _user, _b);
        for (var retry = 0; retry < PlaybackLimits.Reconnections; retry++)
        {
            _fixture.Coordinator.ReportFailure(session);
        }

        var outcome = await OpenAsync(_single, _user, _b);

        Assert.Equal(PlaybackOutcomeKind.Remote, outcome.Kind);
    }

    [Fact]
    public async Task The_drops_of_every_version_of_the_title_count_together()
    {
        for (var drop = 0; drop <= PlaybackLimits.Reconnections; drop++)
        {
            await PlayAndDropAsync(drop % 2 == 0 ? _single : _b, _user, drop % 2 == 0 ? _b : _single);
        }

        var outcome = await OpenAsync(_b, _user, _single);

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
    }

    [Fact]
    public async Task The_drops_of_another_title_or_another_user_do_not_count()
    {
        var other = ServerStub("c", _serverA, "c1");
        for (var drop = 0; drop <= PlaybackLimits.Reconnections; drop++)
        {
            await PlayAndDropAsync(other, _user);
        }

        Assert.Equal(PlaybackOutcomeKind.Remote, (await OpenAsync(_single, _user, _b)).Kind);
        Assert.Equal(PlaybackOutcomeKind.Remote, (await OpenAsync(other, _otherUser)).Kind);
    }

    [Fact]
    public async Task A_drop_closes_the_remote_transcode_and_gives_the_slot_back_without_waiting_for_the_player()
    {
        _fixture.Registry.AddJellyMesh(_serverA);
        var session = await PlayAndDropAsync(_single, _user);

        await _fixture.Coordinator.CloseFailedAsync();

        Assert.Equal([session.RemotePlaySessionId!], _fixture.Client.Stopped);
        Assert.Equal([session.RemotePlaySessionId!], _fixture.Client.Released);
    }

    [Fact]
    public async Task A_drop_keeps_the_session_so_that_its_retries_still_get_an_error()
    {
        var session = await PlayAndDropAsync(_single, _user);

        await _fixture.Coordinator.CloseFailedAsync();

        Assert.Same(session, _fixture.Sessions.Find(session.Id));
        Assert.True(session.HasFailed);
    }

    [Fact]
    public async Task A_session_that_plays_is_not_closed()
    {
        _fixture.Registry.AddJellyMesh(_serverA);
        await OpenAsync(_single, _user);

        await _fixture.Coordinator.CloseFailedAsync();

        Assert.Empty(_fixture.Client.Stopped);
        Assert.Empty(_fixture.Client.Released);
    }

    [Fact]
    public async Task The_retries_of_a_player_do_not_close_the_remote_again()
    {
        _fixture.Registry.AddJellyMesh(_serverA);
        var session = await PlayAndDropAsync(_single, _user);
        await _fixture.Coordinator.CloseFailedAsync();

        session.TryBeginRequest(_fixture.Clock)!.Dispose();
        _fixture.Coordinator.ReportFailure(session);
        await _fixture.Coordinator.CloseFailedAsync();

        Assert.Single(_fixture.Client.Stopped);
        Assert.Single(_fixture.Client.Released);
    }

    [Fact]
    public async Task The_end_of_a_dropped_session_closes_the_remote_again_when_the_first_close_found_it_down()
    {
        _fixture.Registry.AddJellyMesh(_serverA);
        var session = await PlayAndDropAsync(_single, _user);
        _fixture.Registry.Offline.Add(_serverA);
        await _fixture.Coordinator.CloseFailedAsync();
        _fixture.Registry.Offline.Remove(_serverA);

        await _fixture.Coordinator.CloseAsync(session.Id);

        Assert.Equal([session.RemotePlaySessionId!], _fixture.Client.Stopped);
        Assert.Equal([session.RemotePlaySessionId!], _fixture.Client.Released);
    }

    [Fact]
    public async Task A_dropped_session_ended_before_its_close_is_closed_once()
    {
        _fixture.Registry.AddJellyMesh(_serverA);
        var session = await PlayAndDropAsync(_single, _user);

        await _fixture.Coordinator.CloseAsync(session.Id);
        await _fixture.Coordinator.CloseFailedAsync();

        Assert.Single(_fixture.Client.Stopped);
        Assert.Single(_fixture.Client.Released);
    }

    [Fact]
    public async Task The_title_plays_again_five_minutes_after_the_drops()
    {
        for (var drop = 0; drop <= PlaybackLimits.Reconnections; drop++)
        {
            await PlayAndDropAsync(_single, _user, _b);
        }

        _fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await _fixture.Coordinator.SweepAsync();

        Assert.Equal(PlaybackOutcomeKind.Remote, (await OpenAsync(_single, _user, _b)).Kind);
    }
}
