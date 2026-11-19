using System;
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
            "Path": "http://remote.invalid:8096/Videos/1/stream?api_key=tok3n0123456789sentinel",
            "DirectStreamUrl": "http://remote.invalid:8096/stream?api_key=tok3n0123456789sentinel",
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
    public async Task TheIndexTablesHoldNoTokenNoAddressAndNoPath()
    {
        var item = JsonSerializer.Deserialize<RemoteItem>(HostileItem, _options)!;
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, item);
        await _fixture.AddServerAsync("Friend", TestLibraries.Films);

        await _fixture.Sync.RunAsync(CancellationToken.None);

        var content = await _fixture.Database.DumpAsync("index_");
        Assert.Contains("\"source\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Token, content, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Password, content, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Host, content, StringComparison.Ordinal);
        Assert.DoesNotContain("/secret/path", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Friend", content, StringComparison.Ordinal);
    }

    // A photo that tells where it was taken, and a book whose path holds a token.
    private const string HostilePhoto = """
        {
          "Id": "photo", "Name": "IMG_0001", "Type": "Photo", "Path": "/secret/path/IMG_0001.jpg", "ParentId": "album",
          "Width": 640, "Height": 480, "Latitude": 45.4512345, "Longitude": 9.1834567, "Altitude": 120.5432
        }
        """;

    private const string HostileBook = """
        { "Id": "book", "Name": "Libro", "Type": "Book", "Path": "http://remote.invalid:8096/books/x.epub?api_key=tok3n0123456789sentinel" }
        """;

    [Fact]
    public async Task TheIndexTablesHoldNoPathAndNoPlaceOfTheNewTypes()
    {
        const string Home = "40de0000000000000000000000000001";
        const string Books = "40de0000000000000000000000000002";
        _fixture.Client.Libraries.Add(new RemoteLibrary(Home, "Home", "homevideos"));
        _fixture.Client.Libraries.Add(new RemoteLibrary(Books, "Libri", "books"));
        _fixture.Client.Add(Home, RemoteItemType.Photo, JsonSerializer.Deserialize<RemoteItem>(HostilePhoto, _options)!);
        _fixture.Client.Add(Books, RemoteItemType.Book, JsonSerializer.Deserialize<RemoteItem>(HostileBook, _options)!);
        await _fixture.AddServerAsync("Friend", Home, Books);

        await _fixture.Sync.RunAsync(CancellationToken.None);

        var content = await _fixture.Database.DumpAsync("index_");
        Assert.Contains("\"photo\"", content, StringComparison.Ordinal);
        Assert.Contains("\"book\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Token, content, StringComparison.Ordinal);
        Assert.DoesNotContain("remote.invalid", content, StringComparison.Ordinal);
        Assert.DoesNotContain("/secret/path", content, StringComparison.Ordinal);
        Assert.DoesNotContain("tok3n0123456789sentinel", content, StringComparison.Ordinal);
        Assert.DoesNotContain("4512345", content, StringComparison.Ordinal);
        Assert.DoesNotContain("1834567", content, StringComparison.Ordinal);
        Assert.DoesNotContain("5432", content, StringComparison.Ordinal);
    }
}
