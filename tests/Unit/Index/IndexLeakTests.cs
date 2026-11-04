using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Index;

public sealed class IndexLeakTests : IDisposable
{
    private static readonly JsonSerializerOptions _options = new() { PropertyNameCaseInsensitive = true };

    private readonly SyncFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    // What a real answer may hold: paths, a direct stream URL with a key, and the same on an external subtitle.
    private const string HostileItem = """
        {
          "Id": "item", "Name": "Film", "Type": "Movie", "Path": "/secret/path/film.mkv",
          "ProviderIds": { "Tmdb": "1" },
          "Chapters": [{ "Name": "Uno", "StartPositionTicks": 0, "ImagePath": "/secret/path/chapter.jpg" }],
          "MediaSources": [{
            "Id": "source",
            "Path": "http://friend.tailnet.ts.net:8096/Videos/1/stream?api_key=tok3n0123456789sentinel",
            "DirectStreamUrl": "http://friend.tailnet.ts.net:8096/stream?api_key=tok3n0123456789sentinel",
            "TranscodingUrl": "/transcode?ApiKey=tok3n0123456789sentinel",
            "RequiredHttpHeaders": { "Authorization": "tok3n0123456789sentinel" },
            "MediaStreams": [
              { "Type": "Video", "Codec": "h264", "Height": 1080, "BitRate": 1000, "VideoRange": "SDR", "IsDefault": true,
                "Path": "/secret/path/video" },
              { "Type": "Subtitle", "Index": 1, "IsExternal": true, "Path": "/secret/path/film.srt", "DeliveryUrl": "/Videos/1/subs?api_key=tok3n0123456789sentinel" }
            ]
          }]
        }
        """;

    [Fact]
    public async Task TheIndexFileHoldsNoTokenNoAddressAndNoPath()
    {
        var item = JsonSerializer.Deserialize<RemoteItem>(HostileItem, _options)!;
        _fixture.Client.Add("films", RemoteItemType.Movie, item);
        await _fixture.AddServerAsync("Friend", "films");

        await _fixture.Sync.RunAsync(CancellationToken.None);

        var content = await File.ReadAllTextAsync(Path.Combine(_fixture.Folder, IndexFile.FileName));
        Assert.Contains("\"source\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Token, content, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Password, content, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Host, content, StringComparison.Ordinal);
        Assert.DoesNotContain("/secret/path", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Friend", content, StringComparison.Ordinal);
    }
}
