using System;
using System.Net;
using Jellyfin.Plugin.JellyMesh.Playback;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

public class PlaybackRoutesTests
{
    private const string StubId = "0f5a64432ea2468e99dfe6f2749fe353";

    [Theory]
    [InlineData(null, "http://127.0.0.1:8096/JellyMesh/Play/" + StubId)]
    [InlineData("", "http://127.0.0.1:8096/JellyMesh/Play/" + StubId)]
    [InlineData("jf", "http://127.0.0.1:8096/jf/JellyMesh/Play/" + StubId)]
    [InlineData("/jf/", "http://127.0.0.1:8096/jf/JellyMesh/Play/" + StubId)]
    public void A_strm_url_is_loopback_and_reads_back(string? baseUrl, string expected)
    {
        var url = PlaybackRoutes.StrmUrl(8096, baseUrl, StubId);

        Assert.Equal(expected, url);
        Assert.True(PlaybackRoutes.TryGetStubId(url, out var parsed));
        Assert.Equal(StubId, parsed);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a?b")]
    [InlineData("")]
    public void A_stub_id_must_be_url_safe(string stubId)
    {
        Assert.Throws<ArgumentException>(() => PlaybackRoutes.StrmUrl(8096, null, stubId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/federata/Film/x.strm")]
    [InlineData("http://127.0.0.1:8096/Videos/x/stream")]
    [InlineData("http://127.0.0.1:8096/JellyMesh/Play/")]
    [InlineData("http://127.0.0.1:8096/JellyMesh/Play/a/b")]
    public void Anything_else_is_not_a_stub(string? url)
    {
        Assert.False(PlaybackRoutes.TryGetStubId(url, out _));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8096/JellyMesh/Play/abc", true)]
    [InlineData("https://friend.ts.net/jf/jellymesh/play/abc", true)]
    [InlineData("http://example.com/film.mkv", false)]
    [InlineData("/media/film/JellyMesh/Play/x.mkv", false)]
    [InlineData(null, false)]
    public void Decision_11_recognizes_the_endpoint_of_any_server(string? url, bool expected)
    {
        Assert.Equal(expected, PlaybackRoutes.IsPlaybackEndpoint(url));
    }

    private static DefaultHttpContext Request(string address, params string[] headers)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        foreach (var header in headers)
        {
            context.Request.Headers[header] = "127.0.0.1";
        }

        return context;
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void The_server_itself_is_served(string address)
    {
        Assert.True(LoopbackGuard.IsLocalRequest(Request(address)));
    }

    [Theory]
    [InlineData("172.17.0.1")]
    [InlineData("100.64.0.9")]
    [InlineData("::ffff:172.17.0.1")]
    public void Anyone_else_is_refused(string address)
    {
        Assert.False(LoopbackGuard.IsLocalRequest(Request(address)));
    }

    [Fact]
    public void A_missing_address_is_refused()
    {
        Assert.False(LoopbackGuard.IsLocalRequest(new DefaultHttpContext()));
    }

    [Theory]
    [InlineData("172.17.0.1", "X-Forwarded-For")]
    [InlineData("127.0.0.1", "X-Forwarded-For")]
    [InlineData("127.0.0.1", "Forwarded")]
    [InlineData("127.0.0.1", "X-Original-For")]
    [InlineData("127.0.0.1", "X-Real-IP")]
    public void Forwarded_headers_never_allow_and_always_refuse(string address, string header)
    {
        Assert.False(LoopbackGuard.IsLocalRequest(Request(address, header)));
    }
}
