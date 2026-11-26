using System;
using System.Text.Json;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

// No cycles for tracks (row 455): 12.4 gives an audio .strm its file path, not its URL, so the folder shape tells it.
public class FederatedTrackMappingTests
{
    private static readonly JsonSerializerOptions _options = new() { PropertyNameCaseInsensitive = true };

    private static IndexedItem Map(string path) => RemoteItemMapper.ToIndexedItem(
        JsonSerializer.Deserialize<RemoteItem>(
            "{ \"Id\": \"t1\", \"Name\": \"X\", \"Path\": " + JsonSerializer.Serialize(path) + ", \"MediaSources\": [{ \"Id\": \"s1\", \"Protocol\": \"File\", \"Container\": \"strm\", \"Path\": " + JsonSerializer.Serialize(path) + " }] }",
            _options)!,
        RemoteItemType.Audio,
        "lib",
        DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("/fed/8e3f1c2a-4b5d-4e6f-8a9b-0c1d2e3f4a5b/a0d1000000000000000000000000000a/Musica/A/B/01 - X.strm")]
    [InlineData("D:\\fed\\8e3f1c2a-4b5d-4e6f-8a9b-0c1d2e3f4a5b\\a0d1000000000000000000000000000a\\Musica\\A\\B\\01 - X.strm")]
    public void ATrackStubOfAnotherJellyMeshKeepsNoVersion(string path)
    {
        Assert.Empty(Map(path).Versions);
    }

    [Theory]
    [InlineData("/music/Musica/A/B/01 - X.flac")]
    [InlineData("/music/8e3f1c2a-4b5d-4e6f-8a9b-0c1d2e3f4a5b/Musica/A/01 - X.flac")]
    [InlineData("/music/8e3f1c2a-4b5d-4e6f-8a9b-0c1d2e3f4a5b/a0d1000000000000000000000000000a/Film/X.flac")]
    public void ATrackOutsideThatShapeKeepsItsVersion(string path)
    {
        Assert.Single(Map(path).Versions);
    }
}
