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

// The chain over the versions a user sees, and the file of a stub that stays on its server (Q44, D22).
public class PlaybackFallbackTests
{
    private static readonly Guid _user = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _otherUser = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid _aItem = Guid.Parse("70000000-0000-0000-0000-00000000000a");
    private static readonly Guid _bItem = Guid.Parse("70000000-0000-0000-0000-00000000000b");
    private static readonly Guid _serverA = Guid.Parse("a0000000-0000-0000-0000-000000000000");
    private static readonly Guid _serverB = Guid.Parse("b0000000-0000-0000-0000-000000000000");

    private readonly CoordinatorFixture _fixture = new();
    private readonly StubEntry _a = ServerStub("a", _serverA, "a1", "a2");
    private readonly StubEntry _b = ServerStub("b", _serverB, "b1", "b2");

    private static StubEntry ServerStub(string id, Guid server, params string[] items) => new()
    {
        Id = id,
        ServerId = server,
        Kind = StubKind.Movie,
        RelativePath = server.ToString("D") + "/Film/x/x - " + id + ".strm",
        Sources = items.Select(item => new StubSource(server, item, item + "-ms")).ToList(),
        IdentityKey = "key"
    };

    private static PlaybackIdentity IdentityOf(Guid user, StubEntry stub) => new(user, "device", stub.Id == "a" ? _aItem : _bItem, stub.Id);

    private Task<PlaybackOutcome> OpenAsync(StubEntry stub, Guid user, params StubEntry[] fallbacks)
    {
        return _fixture.Coordinator.OpenAsync(
            new PlaybackOpenRequest
            {
                Identity = IdentityOf(user, stub),
                Stub = stub,
                Fallbacks = fallbacks.Select(fallback => new PlaybackCandidate(IdentityOf(user, fallback).ItemId, fallback)).ToList(),
                LocalToken = PlaybackValues.LocalToken
            },
            CancellationToken.None);
    }

    private async Task<string?> FileOpenedAsync(StubEntry stub)
    {
        _fixture.Client.Opened.Clear();
        using var content = await _fixture.Coordinator.OpenFileAsync(stub, HttpMethod.Get, null, CancellationToken.None);
        return content is null ? null : _fixture.Client.Opened.Last();
    }

    private void Fails(params string[] items)
    {
        _fixture.Client.Answer = (request, _) => Task.FromResult(items.Contains(request.ItemId)
            ? RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.ServerError)
            : FakePlaybackClient.Hls(request));
    }

    [Fact]
    public async Task When_the_stub_cannot_serve_the_next_version_serves_under_its_own_id()
    {
        _fixture.Registry.Offline.Add(_serverA);

        var outcome = await OpenAsync(_a, _user, _b);

        Assert.Equal(PlaybackOutcomeKind.Remote, outcome.Kind);
        Assert.Equal(IdentityOf(_user, _b), outcome.Served);
        Assert.Equal(_bItem.ToString("N"), Assert.Single(outcome.Response!.MediaSources).Id);
        Assert.Equal(IdentityOf(_user, _b), Assert.Single(_fixture.Sessions.All).Identity);
        Assert.Equal(["b1"], _fixture.Client.Requests.Select(request => request.ItemId));
    }

    [Fact]
    public async Task Asking_again_after_a_fallback_replaces_the_session_of_the_version_that_served()
    {
        _fixture.Registry.Offline.Add(_serverA);
        await OpenAsync(_a, _user, _b);
        var first = Assert.Single(_fixture.Sessions.All);

        await OpenAsync(_a, _user, _b);

        Assert.NotEqual(first.Id, Assert.Single(_fixture.Sessions.All).Id);
        Assert.Equal(first.RemotePlaySessionId, Assert.Single(_fixture.Client.Stopped));
    }

    [Fact]
    public async Task A_session_left_on_another_version_does_not_drop_the_indexes_of_the_version_asked()
    {
        await OpenAsync(_b, _user);
        var old = Assert.Single(_fixture.Sessions.All);
        _fixture.Client.Requests.Clear();

        await _fixture.Coordinator.OpenAsync(
            new PlaybackOpenRequest
            {
                Identity = IdentityOf(_user, _a),
                Stub = _a,
                Fallbacks = [new PlaybackCandidate(_bItem, _b)],
                LocalToken = PlaybackValues.LocalToken,
                AudioStreamIndex = 3
            },
            CancellationToken.None);

        var request = Assert.Single(_fixture.Client.Requests);
        Assert.Equal("a1", request.ItemId);
        Assert.Equal(3, request.AudioStreamIndex);
        Assert.DoesNotContain(old, _fixture.Sessions.All);
    }

    [Fact]
    public async Task The_other_versions_are_tried_after_every_source_of_the_stub()
    {
        Fails("a1", "a2", "b1");

        var outcome = await OpenAsync(_a, _user, _b);

        Assert.Equal(["a1", "a2", "b1", "b2"], _fixture.Client.Requests.Select(request => request.ItemId));
        Assert.Equal(IdentityOf(_user, _b), outcome.Served);
        Assert.Equal("b2", Assert.Single(_fixture.Sessions.All).Source.ItemId);
    }

    [Fact]
    public async Task The_stub_that_serves_is_named_without_a_fallback()
    {
        var outcome = await OpenAsync(_a, _user, _b);

        Assert.Equal(IdentityOf(_user, _a), outcome.Served);
        Assert.Equal(["a1"], _fixture.Client.Requests.Select(request => request.ItemId));
    }

    [Fact]
    public async Task A_local_transcode_after_a_fallback_reads_the_file_of_the_version_that_serves()
    {
        _fixture.Registry.Offline.Add(_serverA);
        Fails("b1", "b2");

        var outcome = await OpenAsync(_a, _user, _b);

        Assert.Equal(PlaybackOutcomeKind.LocalTranscode, outcome.Kind);
        Assert.Equal(_b.Sources[0], outcome.FileSource);
        Assert.Equal(IdentityOf(_user, _b), outcome.Served);
        Assert.Contains("/Videos/b1/stream?static=true", await FileOpenedAsync(_b), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_version_at_its_stream_limit_is_a_refusal_for_the_limit()
    {
        _fixture.Client.Answer = (_, _) => Task.FromResult(RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.StreamLimitReached));

        var outcome = await OpenAsync(_a, _user, _b);

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
        Assert.True(outcome.AtStreamLimit);
    }

    [Fact]
    public async Task A_version_down_means_the_limit_of_another_is_not_the_whole_story()
    {
        _fixture.Registry.Offline.Add(_serverA);
        _fixture.Client.Answer = (_, _) => Task.FromResult(RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.StreamLimitReached));

        var outcome = await OpenAsync(_a, _user, _b);

        Assert.False(outcome.AtStreamLimit);
    }

    [Fact]
    public async Task The_fallback_of_another_user_never_takes_a_user_or_the_file_to_a_server_they_do_not_see()
    {
        _fixture.Registry.Offline.Add(_serverA);
        await OpenAsync(_a, _otherUser, _b);
        _fixture.Client.Requests.Clear();

        var outcome = await OpenAsync(_a, _user);

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
        Assert.Empty(_fixture.Client.Requests);
        Assert.Null(await FileOpenedAsync(_a));
        Assert.Empty(_fixture.Client.Opened);
    }

    [Fact]
    public async Task The_file_of_a_stub_of_one_server_never_opens_a_source_on_another()
    {
        var stub = ServerStub("a", _serverA, "a1") with { Sources = [new StubSource(_serverA, "a1", "a1-ms"), new StubSource(_serverB, "b1", "b1-ms")] };
        _fixture.Registry.Offline.Add(_serverA);

        Assert.Null(await FileOpenedAsync(stub));
        Assert.Empty(_fixture.Client.Opened);
    }

    [Fact]
    public async Task The_file_opens_first_the_source_its_playback_chose()
    {
        Fails("a1");
        await OpenAsync(_a, _user);

        Assert.Contains("/Videos/a2/stream?static=true", await FileOpenedAsync(_a), StringComparison.Ordinal);
        Assert.Single(_fixture.Client.Opened);
    }

    [Fact]
    public async Task The_file_of_a_version_served_in_fallback_opens_its_chosen_source()
    {
        _fixture.Registry.Offline.Add(_serverA);
        Fails("b1");
        await OpenAsync(_a, _user, _b);

        Assert.Contains("/Videos/b2/stream?static=true", await FileOpenedAsync(_b), StringComparison.Ordinal);
        Assert.Single(_fixture.Client.Opened);
    }

    [Fact]
    public async Task A_chosen_source_is_forgotten_after_the_session_retention()
    {
        Fails("a1");
        await OpenAsync(_a, _user);

        _fixture.Clock.Advance(PlaybackTimeouts.SessionRetention);
        await _fixture.Coordinator.SweepAsync();

        Assert.Contains("/Videos/a1/stream?static=true", await FileOpenedAsync(_a), StringComparison.Ordinal);
    }
}
