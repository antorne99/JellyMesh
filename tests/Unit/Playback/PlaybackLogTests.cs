using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

/// <summary>Checks that log lines carry no value of a remote.</summary>
internal static class PlaybackLogAssert
{
    private static readonly string[] _remoteValues =
    [
        PlaybackValues.RemoteToken,
        PlaybackValues.RemotePlaySession,
        PlaybackValues.RemoteDevice,
        "aaaa-bbbb",
        "secret",
        "remote.example",
        "ApiKey"
    ];

    public static void NoRemoteValue(IEnumerable<string> lines)
    {
        foreach (var value in _remoteValues)
        {
            Assert.DoesNotContain(lines, line => line.Contains(value, StringComparison.OrdinalIgnoreCase));
        }
    }
}

/// <summary>What the coordinator says about each step of a playback.</summary>
public class PlaybackLogTests
{
    private readonly CapturingLogger<PlaybackCoordinator> _logger = new();
    private readonly CoordinatorFixture _fixture;

    public PlaybackLogTests()
    {
        _fixture = new CoordinatorFixture(logger: _logger);
    }

    [Fact]
    public async Task A_playback_that_starts_says_from_which_server_and_how()
    {
        var source = PlaybackValues.Source();

        await _fixture.OpenAsync(PlaybackValues.Stub(source), PlaybackValues.WebProfile);

        Assert.Contains(_logger.Lines, line => line.Contains("plays from server " + source.ServerId + " as Hls", StringComparison.Ordinal));
        PlaybackLogAssert.NoRemoteValue(_logger.Lines);
    }

    [Fact]
    public async Task A_failed_playback_info_says_why_with_its_status_and_that_nothing_could_play()
    {
        var source = PlaybackValues.Source();
        _fixture.Client.Answer = (_, _) => Task.FromResult(RemoteResult.FailWithStatus<PlaybackInfoResponse>(RemoteFailure.ServerError, 500));
        _fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.FailWithStatus<RemoteContent>(RemoteFailure.ServerError, 500));

        var outcome = await _fixture.OpenAsync(PlaybackValues.Stub(source), PlaybackValues.WebProfile);

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
        Assert.Contains(_logger.Lines, line => line.Contains("PlaybackInfo on server " + source.ServerId + " failed: ServerError (HTTP 500)", StringComparison.Ordinal));
        Assert.Contains(_logger.Lines, line => line.Contains("No source can play title stub", StringComparison.Ordinal));
        PlaybackLogAssert.NoRemoteValue(_logger.Lines);
    }

    [Fact]
    public async Task A_first_segment_that_does_not_open_names_the_step_and_its_status()
    {
        var source = PlaybackValues.Source();
        _fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.FailWithStatus<RemoteContent>(RemoteFailure.ServerError, 404));

        await _fixture.OpenAsync(PlaybackValues.Stub(source), PlaybackValues.WebProfile);

        Assert.Contains(_logger.Lines, line => line.Contains("The first HLS segment of server " + source.ServerId + " did not open: ServerError (HTTP 404)", StringComparison.Ordinal));
        Assert.Contains(_logger.Lines, line => line.Contains("The file for a local transcode of server", StringComparison.Ordinal));
        PlaybackLogAssert.NoRemoteValue(_logger.Lines);
    }

    [Fact]
    public async Task A_master_playlist_that_cannot_be_read_says_so()
    {
        _fixture.Client.Playlists = path => path.Contains("master", StringComparison.Ordinal) ? RemoteFailure.Timeout : RemoteFailure.None;

        await _fixture.OpenAsync(PlaybackValues.Stub(PlaybackValues.Source()), PlaybackValues.WebProfile);

        Assert.Contains(_logger.Lines, line => line.Contains("HLS master playlist", StringComparison.Ordinal) && line.Contains("Timeout", StringComparison.Ordinal));
        PlaybackLogAssert.NoRemoteValue(_logger.Lines);
    }
}
