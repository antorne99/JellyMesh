using System;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

public class TrailerMappingTests
{
    private static string[] IdsOf(params string?[] urls)
    {
        var film = TestItems.Movie("m") with { RemoteTrailers = urls.Select(url => url is null ? null : new RemoteMediaUrl(url)).ToList() };
        return RemoteItemMapper.ToIndexedItem(film, RemoteItemType.Movie, "films", DateTimeOffset.UnixEpoch).TrailerIds.ToArray();
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("http://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?feature=share&v=dQw4w9WgXcQ")]
    [InlineData("https://WWW.YouTube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=10")]
    public void AYouTubeTrailerKeepsOnlyItsId(string url)
    {
        Assert.Equal(["dQw4w9WgXcQ"], IdsOf(url));
    }

    [Theory]
    [InlineData("https://vimeo.com/123456789")]
    [InlineData("https://www.youtube.com.evil.example/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://evilyoutube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("ftp://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://100.64.0.1:8096/Videos/x/stream?api_key=secret")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXc")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgX%3CQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ/more")]
    [InlineData("plugin://plugin.video.youtube/play/?video_id=dQw4w9WgXcQ")]
    [InlineData("not a url")]
    [InlineData("")]
    public void AnythingElseIsLeftOut(string url)
    {
        Assert.Empty(IdsOf(url));
    }

    [Theory]
    [InlineData("dQw4w9WgXcQ\n")]
    [InlineData("dQw4w9WgXcQ\r\n")]
    public void AnIdWithATrailingNewLineIsNotAnId(string id)
    {
        Assert.False(YouTubeTrailer.IsId(id));
    }

    [Fact]
    public void ANullEntryIsSkippedAndTheRestKept()
    {
        Assert.Equal(["dQw4w9WgXcQ"], IdsOf(null, "https://youtu.be/dQw4w9WgXcQ"));
    }

    [Fact]
    public void AnIdIsKeptOnceAndTheListIsCapped()
    {
        var urls = Enumerable.Range(0, 40).Select(index => $"https://youtu.be/aaaaaaaaa{index:D2}").Prepend("https://youtu.be/aaaaaaaaa00").ToArray();

        var ids = IdsOf(urls);

        Assert.Equal(20, ids.Length);
        Assert.Equal(ids.Distinct(StringComparer.Ordinal), ids);
        Assert.Equal("aaaaaaaaa00", ids[0]);
    }

    [Fact]
    public void AFilmWithoutTrailersHasNone()
    {
        Assert.Empty(RemoteItemMapper.ToIndexedItem(TestItems.Movie("m"), RemoteItemType.Movie, "films", DateTimeOffset.UnixEpoch).TrailerIds);
    }
}
