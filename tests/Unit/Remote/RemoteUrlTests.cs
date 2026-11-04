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
}
