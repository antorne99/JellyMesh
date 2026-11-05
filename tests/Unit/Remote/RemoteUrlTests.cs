using System;
using Jellyfin.Plugin.JellyMesh.Remote;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Remote;

public class RemoteUrlTests
{
    [Theory]
    [InlineData("http://server:8096", "http://server:8096")]
    [InlineData("http://server:8096/", "http://server:8096")]
    [InlineData("  https://server.tailnet.ts.net/jellyfin/ ", "https://server.tailnet.ts.net/jellyfin")]
    [InlineData("http://100.64.0.1:8096", "http://100.64.0.1:8096")]
    public void AcceptsAndNormalizes(string input, string expected)
    {
        Assert.True(RemoteUrl.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("server:8096")]
    [InlineData("ftp://server")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://user:secret@server")]
    [InlineData("http://server?api_key=abc")]
    [InlineData("http://server#fragment")]
    public void Refuses(string? input)
    {
        Assert.False(RemoteUrl.TryNormalize(input, out _));
    }

    private static readonly Uri _base = new("http://friend.tailnet:8096/jf/");

    [Theory]
    [InlineData("/videos/a/master.m3u8?x=1", "http://friend.tailnet:8096/jf/videos/a/master.m3u8?x=1")]
    [InlineData("videos/a/0.ts", "http://friend.tailnet:8096/jf/videos/a/0.ts")]
    [InlineData("/a?next=http://evil.example/", "http://friend.tailnet:8096/jf/a?next=http://evil.example/")]
    public void A_path_stays_under_the_base_address(string path, string expected)
    {
        Assert.True(RemoteUrl.TryCombine(_base, path, out var url));
        Assert.Equal(expected, url.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://evil.example/x")]
    [InlineData("https://evil.example/x")]
    [InlineData("/http://evil.example/x")]
    [InlineData("//evil.example/x")]
    [InlineData("\\\\evil.example\\x")]
    [InlineData("/\\evil.example/x")]
    [InlineData("/../x")]
    [InlineData("/a/../../x")]
    [InlineData("/a/%2e%2e/x")]
    [InlineData("/a/%2E%2e/%2e%2e/x")]
    [InlineData("/a\r\nHost: evil")]
    [InlineData("")]
    public void A_path_that_would_lead_elsewhere_is_refused(string path)
    {
        Assert.False(RemoteUrl.TryCombine(_base, path, out _));
    }

    [Fact]
    public void A_path_longer_than_any_a_server_writes_is_refused()
    {
        Assert.False(RemoteUrl.TryCombine(_base, "/" + new string('a', 70_000), out _));
    }
}
