using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Playback;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

public class HlsRewritingTests
{
    private const string Folder = "/videos/aaaa-bbbb/";
    private static readonly string[] _secrets = [PlaybackValues.RemotePlaySession, PlaybackValues.RemoteDevice, PlaybackValues.RemoteToken];

    private static string Rewrite(PlaybackSession session, string playlist, string prefix = "f/")
    {
        return HlsPlaylistRewriter.Rewrite(playlist, session, Folder, prefix, PlaybackValues.LocalToken, _secrets)!;
    }

    private static IEnumerable<KeyValuePair<string, string>> Query(params (string Key, string Value)[] pairs)
    {
        return pairs.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value));
    }

    [Fact]
    public void Master_keeps_no_remote_value_and_gives_every_uri_the_local_token()
    {
        var text = Rewrite(PlaybackValues.Session(), PlaybackValues.MasterPlaylist);

        foreach (var secret in _secrets.Append("DeviceId").Append("PlaySessionId").Append("ms/Subtitles"))
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }

        Assert.Equal(2, text.Split("ApiKey=" + PlaybackValues.LocalToken).Length - 1);
        Assert.Contains("#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"Ita\",URI=\"f/0/subtitles.m3u8?SegmentLength=30&ApiKey=", text, StringComparison.Ordinal);
        Assert.Contains("CODECS=\"vp09.00.10.08,opus\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Variant_rewrites_the_map_attribute_and_keeps_segment_timing()
    {
        var text = Rewrite(PlaybackValues.Session(), PlaybackValues.VariantPlaylist, "../");

        Assert.Contains("#EXT-X-MAP:URI=\"../0/-1.mp4?runtimeTicks=0&actualSegmentLengthTicks=0&ApiKey=", text, StringComparison.Ordinal);
        Assert.Contains("\n../0/0.mp4?runtimeTicks=0&actualSegmentLengthTicks=30000000&ApiKey=", text, StringComparison.Ordinal);
        Assert.DoesNotContain(PlaybackValues.RemotePlaySession, text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_remote_query_comes_back_for_upstream_and_only_timing_from_the_client()
    {
        var session = PlaybackValues.Session();
        Rewrite(session, PlaybackValues.VariantPlaylist, "../");

        var upstream = session.FindUpstream(0, "12.mp4", Query(("runtimeTicks", "360000000"), ("actualSegmentLengthTicks", "30000000"), ("PlaySessionId", "forged"), ("DeviceId", "forged")));

        Assert.NotNull(upstream);
        Assert.StartsWith("/videos/aaaa-bbbb/hls1/main/12.mp4?DeviceId=" + PlaybackValues.RemoteDevice, upstream, StringComparison.Ordinal);
        Assert.Contains("PlaySessionId=" + PlaybackValues.RemotePlaySession, upstream, StringComparison.Ordinal);
        Assert.Contains("runtimeTicks=360000000", upstream, StringComparison.Ordinal);
        Assert.DoesNotContain("forged", upstream, StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_session_never_handed_out_is_not_found()
    {
        var session = PlaybackValues.Session();
        Rewrite(session, PlaybackValues.VariantPlaylist, "../");

        Assert.Null(session.FindUpstream(9, "0.mp4", []));
        Assert.Null(session.FindUpstream(0, "other.bin", []));
        Assert.Null(session.FindUpstream(0, "..", []));
    }

    [Theory]
    [InlineData("http://remote.example/seg.ts")]
    [InlineData("//remote.example/seg.ts")]
    [InlineData("/videos/rooted/seg.ts")]
    [InlineData("../../Users/seg.ts")]
    public void A_uri_that_could_leave_the_folder_refuses_the_whole_playlist(string uri)
    {
        Assert.Null(HlsPlaylistRewriter.Rewrite("#EXTM3U\n" + uri + "\n", PlaybackValues.Session(), Folder, "f/", PlaybackValues.LocalToken, _secrets));
    }

    [Fact]
    public void A_remote_value_left_outside_a_uri_refuses_the_whole_playlist()
    {
        var playlist = "#EXTM3U\n#EXT-X-CUSTOM:" + PlaybackValues.RemoteToken + "\n";

        Assert.Null(HlsPlaylistRewriter.Rewrite(playlist, PlaybackValues.Session(), Folder, "f/", PlaybackValues.LocalToken, _secrets));
    }

    [Fact]
    public void A_subtitle_is_reachable_in_the_format_the_client_asks_for()
    {
        var session = PlaybackValues.Session();

        var url = session.MapRootedUrl("/Videos/item/ms/Subtitles/2/0/Stream.vtt?ApiKey=" + PlaybackValues.RemoteToken, PlaybackValues.LocalToken);

        Assert.Equal("/JellyMesh/Playback/" + session.Id + "/f/0/Stream.vtt?ApiKey=" + PlaybackValues.LocalToken, url);
        Assert.Equal("/Videos/item/ms/Subtitles/2/0/Stream.js", session.FindUpstream(0, "Stream.js", []));
    }

    [Theory]
    [InlineData("http://remote.example/Stream.vtt")]
    [InlineData("Stream.vtt")]
    public void A_subtitle_url_that_is_not_server_relative_is_refused(string url)
    {
        Assert.Null(PlaybackValues.Session().MapRootedUrl(url, PlaybackValues.LocalToken));
    }

    [Fact]
    public void Variants_list_their_codecs()
    {
        var variant = Assert.Single(HlsMaster.ReadVariants(PlaybackValues.MasterPlaylist));

        Assert.StartsWith("main.m3u8", variant.Uri, StringComparison.Ordinal);
        Assert.Equal(["vp09.00.10.08", "opus"], variant.Codecs);
    }

    [Fact]
    public void First_segment_is_the_first_uri_line()
    {
        Assert.StartsWith("hls1/main/0.mp4", HlsMaster.FirstSegment(PlaybackValues.VariantPlaylist), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("vp09.00.10.08,opus", true)]
    [InlineData("av01.0.08M.08,fLaC", true)]
    [InlineData("hvc1.2.4.L153.B0,opus", false)]
    [InlineData("vp09.00.10.08,mp4a.40.2", false)]
    [InlineData("something-new.1", true)]
    public void Codecs_are_compared_with_the_hls_profiles(string codecs, bool playable)
    {
        Assert.Equal(playable, HlsMaster.IsPlayable(new HlsVariant("main.m3u8", codecs.Split(',')), PlaybackValues.WebProfile));
    }

    [Fact]
    public void Without_a_profile_nothing_can_be_ruled_out()
    {
        Assert.True(HlsMaster.IsPlayable(new HlsVariant("main.m3u8", ["hvc1.2.4.L153.B0"]), null));
    }

    [Theory]
    [InlineData("#EXT-X-MAP:URI=http://evil.example/init.mp4")]
    [InlineData("#EXT-X-MAP:URI='http://evil.example/init.mp4'")]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,uri=\"http://evil.example/key\"")]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"http://evil.example/key\"")]
    [InlineData("#EXT-X-MEDIA:TYPE=SUBTITLES,URI=\"//evil.example/subs.m3u8\"")]
    [InlineData("#EXT-X-SESSION-DATA:DATA-ID=\"x\",VALUE=\"https://evil.example\"")]
    [InlineData("#EXT-X-I-FRAME-STREAM-INF:URI=\"/rooted/iframes.m3u8\"")]
    [InlineData("http://evil.example/0.ts")]
    [InlineData("//evil.example/0.ts")]
    [InlineData("../../0.ts")]
    public void A_playlist_that_points_anywhere_else_is_refused_whole(string poison)
    {
        var text = HlsPlaylistRewriter.Rewrite("#EXTM3U\n" + poison + "\n", PlaybackValues.Session(), Folder, "f/", PlaybackValues.LocalToken, _secrets);

        Assert.Null(text);
    }

    [Fact]
    public void A_value_a_client_would_see_must_be_a_plain_one()
    {
        var text = Rewrite(PlaybackValues.Session(), "#EXTM3U\n0.ts?runtimeTicks=http%3A%2F%2Fevil.example&SegmentLength=3\n");

        Assert.DoesNotContain("evil", text, StringComparison.Ordinal);
        Assert.Contains("SegmentLength=3&ApiKey=", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_remote_that_makes_up_new_folders_cannot_grow_a_session_without_end()
    {
        var session = PlaybackValues.Session();
        var refused = 0;
        for (var folder = 0; folder < PlaybackLimits.FoldersPerSession + 10; folder++)
        {
            refused += session.MapRelativeUri("a" + folder + "/0.ts", Folder, "f/", PlaybackValues.LocalToken) is null ? 1 : 0;
        }

        Assert.Equal(10, refused);
        Assert.NotNull(session.MapRelativeUri("a0/1.ts", Folder, "f/", PlaybackValues.LocalToken));
    }

    [Fact]
    public void A_remote_that_makes_up_new_file_names_cannot_grow_a_session_without_end()
    {
        var session = PlaybackValues.Session();
        var refused = 0;
        for (var file = 0; file < PlaybackLimits.FileShapesPerSession + 10; file++)
        {
            refused += session.MapRelativeUri("name" + file + ".ts", Folder, "f/", PlaybackValues.LocalToken) is null ? 1 : 0;
        }

        Assert.Equal(10, refused);
    }

    [Fact]
    public void Numbered_segments_of_a_long_film_stay_one_entry()
    {
        var session = PlaybackValues.Session();

        for (var segment = 0; segment < PlaybackLimits.FileShapesPerSession * 2; segment++)
        {
            Assert.NotNull(session.MapRelativeUri("hls1/main/" + segment + ".ts?runtimeTicks=0", Folder, "f/", PlaybackValues.LocalToken));
        }
    }

    [Fact]
    public void A_master_full_of_empty_variants_is_read_in_linear_time()
    {
        var master = string.Concat(Enumerable.Repeat("#EXT-X-STREAM-INF:BANDWIDTH=1\n", 200_000));

        var variants = HlsMaster.ReadVariants(master);

        Assert.Empty(variants);
    }

    [Fact]
    public void A_session_refuses_requests_over_its_limit_and_takes_them_again_after()
    {
        var session = PlaybackValues.Session();
        var running = Enumerable.Range(0, PlaybackLimits.RequestsPerSession).Select(_ => session.TryBeginRequest(TimeProvider.System)).ToList();

        Assert.All(running, Assert.NotNull);
        Assert.Null(session.TryBeginRequest(TimeProvider.System));

        running[0]!.Dispose();

        Assert.NotNull(session.TryBeginRequest(TimeProvider.System));
    }
}
