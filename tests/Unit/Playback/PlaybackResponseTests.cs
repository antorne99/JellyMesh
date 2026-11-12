using System;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.JellyMesh.Playback;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

public class PlaybackResponseTests
{
    [Fact]
    public void Nothing_of_the_remote_survives_in_what_a_client_receives()
    {
        var session = PlaybackValues.Session();

        var source = PlaybackResponseBuilder.Build(PlaybackValues.HlsAnswer("remote-ms"), session, PlaybackValues.LocalToken);

        var json = JsonSerializer.Serialize(source, JsonDefaults.PascalCaseOptions);
        foreach (var secret in new[] { PlaybackValues.RemoteToken, PlaybackValues.RemotePlaySession, PlaybackValues.RemoteDevice, "secret", "remote-etag", "remote-ms", "remote-item", "/media/", "aaaa-bbbb" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_hls_source_points_to_the_plugin_with_the_local_token_and_stops_the_web_retry()
    {
        var session = PlaybackValues.Session();

        var source = PlaybackResponseBuilder.Build(PlaybackValues.HlsAnswer("ms"), session, PlaybackValues.LocalToken)!;

        Assert.False(source.SupportsDirectPlay);
        Assert.False(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.Null(source.Path);
        Assert.Equal(MediaStreamProtocol.hls, source.TranscodingSubProtocol);
        Assert.Equal(session.Identity.ItemId.ToString("N"), source.Id);
        Assert.StartsWith("/JellyMesh/Playback/" + session.Id + "/master.m3u8?", source.TranscodingUrl, StringComparison.Ordinal);
        Assert.Contains("PlaySessionId=" + session.Id, source.TranscodingUrl, StringComparison.Ordinal);
        Assert.Contains("AllowVideoStreamCopy=false", source.TranscodingUrl, StringComparison.Ordinal);
        Assert.Contains("AllowAudioStreamCopy=false", source.TranscodingUrl, StringComparison.Ordinal);
        Assert.Contains("ApiKey=" + PlaybackValues.LocalToken, source.TranscodingUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void A_direct_source_is_a_stream_url_named_after_the_file()
    {
        var session = PlaybackValues.Session(PlaybackStreamKind.Direct);
        var remote = PlaybackValues.HlsAnswer("ms");
        remote.TranscodingContainer = null;

        var source = PlaybackResponseBuilder.Build(remote, session, PlaybackValues.LocalToken)!;

        Assert.StartsWith("/JellyMesh/Playback/" + session.Id + "/stream.mkv?", source.TranscodingUrl, StringComparison.Ordinal);
        Assert.Contains("CopyTimestamps=true", source.TranscodingUrl, StringComparison.Ordinal);
        Assert.Equal(MediaStreamProtocol.http, source.TranscodingSubProtocol);
        Assert.Equal("mkv", source.TranscodingContainer);
    }

    [Fact]
    public void External_subtitles_come_through_the_session()
    {
        var session = PlaybackValues.Session();

        var source = PlaybackResponseBuilder.Build(PlaybackValues.HlsAnswer("ms"), session, PlaybackValues.LocalToken)!;

        var subtitle = source.MediaStreams.Single(stream => stream.Index == 0);
        Assert.Null(subtitle.Path);
        Assert.False(subtitle.IsExternalUrl);
        Assert.StartsWith("/JellyMesh/Playback/" + session.Id + "/f/", subtitle.DeliveryUrl, StringComparison.Ordinal);
        Assert.EndsWith("ApiKey=" + PlaybackValues.LocalToken, subtitle.DeliveryUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_stream_numbers_are_kept()
    {
        var source = PlaybackResponseBuilder.Build(PlaybackValues.HlsAnswer("ms"), PlaybackValues.Session(), PlaybackValues.LocalToken)!;

        Assert.Equal([0, 1], source.MediaStreams.Select(stream => stream.Index));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Whether_the_source_has_segments_is_copied(bool hasSegments)
    {
        var remote = PlaybackValues.HlsAnswer("ms");
        remote.HasSegments = hasSegments;

        var source = PlaybackResponseBuilder.Build(remote, PlaybackValues.Session(), PlaybackValues.LocalToken)!;

        Assert.Equal(hasSegments, source.HasSegments);
    }

    [Fact]
    public void An_absolute_delivery_url_refuses_the_source()
    {
        var remote = PlaybackValues.HlsAnswer("ms");
        remote.MediaStreams[0].DeliveryUrl = "http://friend.tailnet.ts.net:8096/Stream.vtt";

        Assert.Null(PlaybackResponseBuilder.Build(remote, PlaybackValues.Session(), PlaybackValues.LocalToken));
    }

    [Theory]
    [InlineData("/videos/x/master.m3u8?a=1&ApiKey=tok&b=2", "/videos/x/master.m3u8?a=1&b=2")]
    [InlineData("/videos/x/master.m3u8?ApiKey=tok", "/videos/x/master.m3u8")]
    [InlineData("/videos/x/master.m3u8?api_key=tok&a=1", "/videos/x/master.m3u8?a=1")]
    [InlineData("/videos/x/master.m3u8", "/videos/x/master.m3u8")]
    public void The_token_leaves_a_remote_url(string url, string expected)
    {
        Assert.Equal(expected, PlaybackUrls.WithoutToken(url));
    }

    [Theory]
    [InlineData("/videos/x/master.m3u8?ApiKey=t&AllowVideoStreamCopy=true", "/videos/x/master.m3u8?ApiKey=t&AllowVideoStreamCopy=false&AllowAudioStreamCopy=false")]
    [InlineData("/videos/x/master.m3u8", "/videos/x/master.m3u8?AllowVideoStreamCopy=false&AllowAudioStreamCopy=false")]
    public void A_local_url_gets_both_retry_guards(string url, string expected)
    {
        Assert.Equal(expected, PlaybackUrls.WithoutClientRetry(url));
    }

    [Fact]
    public void The_local_session_id_is_not_the_remote_one()
    {
        var session = PlaybackValues.Session();

        Assert.NotEqual(PlaybackValues.RemotePlaySession, session.Id);
        Assert.Equal(32, session.Id.Length);
    }

    [Fact]
    public void Session_field_list_stays_away_from_attachments()
    {
        var source = PlaybackResponseBuilder.Build(PlaybackValues.HlsAnswer("ms"), PlaybackValues.Session(), PlaybackValues.LocalToken)!;

        Assert.Empty(source.MediaAttachments);
        Assert.IsType<MediaSourceInfo>(source);
        Assert.Null(source.ETag);
    }
}
