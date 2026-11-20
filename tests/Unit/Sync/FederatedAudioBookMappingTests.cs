using System;
using System.Text.Json;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

// No cycles for audiobooks: like a track, an audiobook .strm reports its file path, so the books folder shape tells it.
public class FederatedAudioBookMappingTests
{
    private static readonly JsonSerializerOptions _options = new() { PropertyNameCaseInsensitive = true };

    private static IndexedItem Map(string path) => RemoteItemMapper.ToIndexedItem(
        JsonSerializer.Deserialize<RemoteItem>(
            "{ \"Id\": \"b1\", \"Name\": \"X\", \"Path\": " + JsonSerializer.Serialize(path) + ", \"MediaSources\": [{ \"Id\": \"s1\", \"Protocol\": \"File\", \"Container\": \"strm\", \"Path\": " + JsonSerializer.Serialize(path) + " }] }",
            _options)!,
        RemoteItemType.AudioBook,
        "lib",
        DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("/fed/8e3f1c2a-4b5d-4e6f-8a9b-0c1d2e3f4a5b/a0d1000000000000000000000000000a/Libri/A/B/01 - X.strm")]
    [InlineData("D:\\fed\\8e3f1c2a-4b5d-4e6f-8a9b-0c1d2e3f4a5b\\a0d1000000000000000000000000000a\\Libri\\B\\X.strm")]
    public void AnAudiobookStubOfAnotherJellyMeshKeepsNoVersion(string path)
    {
        Assert.Empty(Map(path).Versions);
    }

    [Theory]
    [InlineData("/books/Libri/A/B/01 - X.m4b")]
    [InlineData("/books/8e3f1c2a-4b5d-4e6f-8a9b-0c1d2e3f4a5b/Libri/A/X.mp3")]
    [InlineData("/books/8e3f1c2a-4b5d-4e6f-8a9b-0c1d2e3f4a5b/a0d1000000000000000000000000000a/Musica/A/B/X.m4b")]
    public void AnAudiobookOutsideThatShapeKeepsItsVersion(string path)
    {
        Assert.Single(Map(path).Versions);
    }
}
