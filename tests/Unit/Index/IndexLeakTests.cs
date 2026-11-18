using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using Microsoft.Data.Sqlite;
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
        _fixture.Client.Add("films", RemoteItemType.Movie, item);
        await _fixture.AddServerAsync("Friend", "films");

        await _fixture.Sync.RunAsync(CancellationToken.None);

        var content = await _fixture.Database.ReadAsync(DumpIndexTables, CancellationToken.None);
        Assert.Contains("\"source\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Token, content, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Password, content, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Host, content, StringComparison.Ordinal);
        Assert.DoesNotContain("/secret/path", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Friend", content, StringComparison.Ordinal);
    }

    // Every value of every row of the index tables, as text.
    private static string DumpIndexTables(SqliteConnection connection, SqliteTransaction transaction)
    {
        var text = new StringBuilder();
        foreach (var query in (string[])["SELECT * FROM index_servers;", "SELECT * FROM index_mark_libraries;", "SELECT * FROM index_items;"])
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    text.Append(reader.GetValue(column)).Append('\n');
                }
            }
        }

        return text.ToString();
    }
}
