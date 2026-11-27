using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

// File and Download of a stub in the session limit of the user (row 505).
public partial class PlaybackFiltersTests
{
    // Opens sessions of a user, each a second after the one before.
    private List<PlaybackSession> SessionsOf(Guid userId, int count)
    {
        var opened = new List<PlaybackSession>();
        for (var device = 0; device < count; device++)
        {
            _fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            var identity = new PlaybackIdentity(userId, "device-" + device, _itemId, "stub");
            var session = new PlaybackSession(identity, _stub.Sources[0], "remote-play-" + device, PlaybackStreamKind.Hls, "/videos/x/master.m3u8", _fixture.Clock.GetUtcNow());
            _fixture.Sessions.Add(session);
            opened.Add(session);
        }

        return opened;
    }

    private static int? StatusOf(ActionExecutingContext context) => (context.Result as StatusCodeResult)?.StatusCode;

    [Theory]
    [InlineData("GetFile")]
    [InlineData("GetDownload")]
    public async Task With_every_session_in_use_a_file_read_is_refused_and_none_is_cut(string action)
    {
        CanSeeItem();
        MayDownload(true);
        RemoteServes();
        var running = SessionsOf(_user.Id, PlaybackLimits.SessionsPerUser).Select(session => session.BeginRequest(_fixture.Clock)).ToList();
        var context = FileRequest(action);

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(context));
        Assert.Empty(BodyOf(context));
        Assert.Empty(_fixture.Client.Opened);
        Assert.Empty(_fixture.Client.Stopped);
        Assert.Equal(PlaybackLimits.SessionsPerUser, _fixture.Sessions.All.Count);
        running.ForEach(request => request.Dispose());
    }

    // Starts file reads of the user that stay open until they are let go.
    private List<Task<bool>> HeldReads(int count, TaskCompletionSource held)
    {
        _fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.Success(new RemoteContent(
            System.Net.HttpStatusCode.OK,
            new RemoteContentHeaders("video/x-matroska", null, null, "bytes"),
            new HeldStream(held))));
        return Enumerable.Range(0, count).Select(_ => RunAsync(GuardFilter(), FileRequest("GetFile"))).ToList();
    }

    [Fact]
    public async Task Running_file_reads_count_with_the_sessions_in_use()
    {
        CanSeeItem();
        MayDownload(true);
        var running = SessionsOf(_user.Id, PlaybackLimits.SessionsPerUser - 2).Select(session => session.BeginRequest(_fixture.Clock)).ToList();
        var held = new TaskCompletionSource();
        var reads = HeldReads(2, held);
        var context = FileRequest("GetDownload");

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(context));
        Assert.Equal(2, _fixture.Client.Opened.Count);

        held.SetResult();
        await Task.WhenAll(reads);
        RemoteServes();
        var later = FileRequest("GetDownload");
        Assert.False(await RunAsync(GuardFilter(), later));
        Assert.Equal(_remoteBytes, BodyOf(later));
        running.ForEach(request => request.Dispose());
    }

    [Fact]
    public async Task A_playback_is_refused_while_file_reads_take_every_place_of_the_user()
    {
        CanSeeItem();
        var held = new TaskCompletionSource();
        var reads = HeldReads(PlaybackLimits.SessionsPerUser, held);

        var refused = await PlayAsync();
        held.SetResult();
        await Task.WhenAll(reads);
        var served = await PlayAsync();

        Assert.Equal(PlaybackOutcomeKind.Unavailable, refused.Kind);
        Assert.Equal(PlaybackRefusal.FailedText, PlaybackRefusal.TextOf(refused));
        Assert.Equal(PlaybackOutcomeKind.Remote, served.Kind);
    }

    [Fact]
    public async Task File_reads_that_start_together_close_a_session_each_and_never_go_over_the_limit()
    {
        CanSeeItem();
        var sessions = SessionsOf(_user.Id, PlaybackLimits.SessionsPerUser);
        var held = new TaskCompletionSource();
        var reads = new List<Task<bool>>();
        var started = false;
        _fixture.Client.OnStop = () =>
        {
            if (!started)
            {
                started = true;
                reads.AddRange(HeldReads(1, held));
            }
        };

        reads.AddRange(HeldReads(1, held));

        Assert.Equal(2, _fixture.Client.Opened.Count);
        Assert.Equal(sessions.Skip(2).Select(session => session.Id).Order(), _fixture.Sessions.All.Select(session => session.Id).Order());
        held.SetResult();
        await Task.WhenAll(reads);
    }

    [Theory]
    [InlineData("GetFile")]
    [InlineData("GetDownload")]
    public async Task At_the_limit_a_file_read_closes_the_oldest_session_not_in_use_and_is_served(string action)
    {
        CanSeeItem();
        MayDownload(true);
        RemoteServes();
        var sessions = SessionsOf(_user.Id, PlaybackLimits.SessionsPerUser);
        using var running = sessions[0].BeginRequest(_fixture.Clock);
        var context = FileRequest(action);

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(_remoteBytes, BodyOf(context));
        Assert.Equal("remote-play-1", Assert.Single(_fixture.Client.Stopped));
        Assert.Null(_fixture.Sessions.Find(sessions[1].Id));
        Assert.Equal(PlaybackLimits.SessionsPerUser - 1, _fixture.Sessions.All.Count);
    }

    [Fact]
    public async Task Under_the_limit_a_file_read_closes_no_session()
    {
        CanSeeItem();
        RemoteServes();
        SessionsOf(_user.Id, PlaybackLimits.SessionsPerUser - 1);
        var context = FileRequest("GetFile");

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(_remoteBytes, BodyOf(context));
        Assert.Equal(PlaybackLimits.SessionsPerUser - 1, _fixture.Sessions.All.Count);
    }

    [Fact]
    public async Task A_file_read_gives_its_place_back_however_it_ends()
    {
        CanSeeItem();
        for (var read = 0; read <= PlaybackLimits.SessionsPerUser; read++)
        {
            _fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.Fail<RemoteContent>(RemoteFailure.Unreachable));
            await RunAsync(GuardFilter(), FileRequest("GetFile"));
            _fixture.Client.Open = (_, _) => throw new OperationCanceledException();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(GuardFilter(), Aborted(FileRequest("GetFile"))));
            RemoteServes();
            var head = FileRequest("GetFile");
            head.HttpContext.Request.Method = HttpMethods.Head;
            await RunAsync(GuardFilter(), head);
        }

        var context = FileRequest("GetFile");
        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(_remoteBytes, BodyOf(context));
    }

    [Fact]
    public async Task A_session_that_fails_to_close_holds_up_neither_the_others_nor_the_place_of_the_read()
    {
        CanSeeItem();
        RemoteServes();
        SessionsOf(_user.Id, PlaybackLimits.SessionsPerUser + 1);
        var failures = 0;
        _fixture.Client.OnStop = () =>
        {
            if (failures++ == 0)
            {
                throw new InvalidOperationException();
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(GuardFilter(), FileRequest("GetFile")));

        Assert.Equal(["remote-play-0", "remote-play-1"], _fixture.Client.Stopped);
        var context = FileRequest("GetFile");
        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(2, _fixture.Client.Stopped.Count);
        Assert.Equal(_remoteBytes, BodyOf(context));
    }

    [Fact]
    public async Task Another_user_at_the_limit_does_not_count()
    {
        CanSeeItem();
        RemoteServes();
        var running = SessionsOf(Guid.NewGuid(), PlaybackLimits.SessionsPerUser).Select(session => session.BeginRequest(_fixture.Clock)).ToList();
        var context = FileRequest("GetFile");

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(_remoteBytes, BodyOf(context));
        Assert.Equal(PlaybackLimits.SessionsPerUser, _fixture.Sessions.All.Count);
        running.ForEach(request => request.Dispose());
    }

    [Fact]
    public async Task The_read_of_the_server_itself_does_not_count()
    {
        CanSeeItem();
        var held = new TaskCompletionSource();
        var reads = HeldReads(PlaybackLimits.SessionsPerUser, held);

        using var atLimit = await _fixture.Coordinator.OpenFileAsync(_stub, HttpMethod.Get, null, CancellationToken.None);
        Assert.NotNull(atLimit);
        held.SetResult();
        await Task.WhenAll(reads);

        var own = new List<RemoteContent?>();
        for (var read = 0; read < PlaybackLimits.SessionsPerUser; read++)
        {
            own.Add(await _fixture.Coordinator.OpenFileAsync(_stub, HttpMethod.Get, null, CancellationToken.None));
        }

        RemoteServes();
        var context = FileRequest("GetFile");
        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(_remoteBytes, BodyOf(context));
        Assert.All(own, Assert.NotNull);
        own.ForEach(content => content?.Dispose());
    }

    [Theory]
    [InlineData("GetFile", false, StatusCodes.Status404NotFound)]
    [InlineData("GetDownload", true, StatusCodes.Status403Forbidden)]
    public async Task A_file_read_refused_for_a_permission_closes_no_session(string action, bool visible, int status)
    {
        if (visible)
        {
            CanSeeItem();
        }

        MayDownload(false);
        SessionsOf(_user.Id, PlaybackLimits.SessionsPerUser);
        var context = FileRequest(action);

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(status, StatusOf(context));
        Assert.Equal(PlaybackLimits.SessionsPerUser, _fixture.Sessions.All.Count);
        Assert.Empty(_fixture.Client.Stopped);
    }

    // A request whose client is already gone.
    private static ActionExecutingContext Aborted(ActionExecutingContext context)
    {
        context.HttpContext.RequestAborted = new CancellationToken(true);
        return context;
    }

    private Task<PlaybackOutcome> PlayAsync()
    {
        var identity = new PlaybackIdentity(_user.Id, "device-play", _itemId, "stub");
        return _fixture.Coordinator.OpenAsync(new PlaybackOpenRequest { Identity = identity, Stub = _stub, LocalToken = Token }, CancellationToken.None);
    }

    // A remote file whose bytes never come until it is let go, then it ends.
    private sealed class HeldStream(TaskCompletionSource held) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await held.Task.WaitAsync(cancellationToken);
            return 0;
        }
    }
}
