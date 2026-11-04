using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

/// <summary>Decision 11: a remote item whose .strm points to a JellyMesh playback endpoint is not imported.</summary>
public sealed class FederatedStubImportTests : IDisposable
{
    private const string StubUrl = "http://127.0.0.1:8096/JellyMesh/Play/0123456789abcdef0123456789abcdef";

    private readonly StubFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static RemoteItem WithPath(RemoteItem item, string? path)
        => item with { MediaSources = item.MediaSources!.Select(source => source with { Path = path }).ToList() };

    [Theory]
    [InlineData(StubUrl)]
    [InlineData("https://other.host:8920/jf/JellyMesh/Play/abc")]
    public void AVersionThatIsAStubOfAnotherServerIsDropped(string path)
    {
        var item = RemoteItemMapper.ToIndexedItem(WithPath(TestItems.Movie("m"), path), RemoteItemType.Movie, "films", DateTimeOffset.UnixEpoch);

        Assert.Empty(item.Versions);
    }

    [Theory]
    [InlineData("/media/film/a.mkv")]
    [InlineData("http://example.com/stream/a.mp4")]
    [InlineData(null)]
    public void AnyOtherPathIsNormalAndIsNotKept(string? path)
    {
        var item = RemoteItemMapper.ToIndexedItem(WithPath(TestItems.Movie("m"), path), RemoteItemType.Movie, "films", DateTimeOffset.UnixEpoch);

        Assert.Single(item.Versions);
    }

    [Fact]
    public void OnlyTheStubVersionOfAnItemIsDropped()
    {
        var film = TestItems.Movie("m") with
        {
            MediaSources = [TestItems.Source("a", 1080, 1000) with { Path = StubUrl }, TestItems.Source("b", 720, 1000) with { Path = "/media/b.mkv" }]
        };

        var item = RemoteItemMapper.ToIndexedItem(film, RemoteItemType.Movie, "films", DateTimeOffset.UnixEpoch);

        Assert.Equal("b", Assert.Single(item.Versions).MediaSourceId);
    }

    [Fact]
    public async Task AStubOfAnotherServerGetsNoStubHere()
    {
        _fixture.Sync.Client.Add(
            "films",
            RemoteItemType.Movie,
            WithPath(TestItems.Movie("stub", "Federated There", 2020, "1"), StubUrl),
            WithPath(TestItems.Movie("real", "Real There", 2021, "2"), "/media/real.mkv"));
        await _fixture.Sync.AddServerAsync("A", "films");

        await _fixture.SyncAndMaterializeAsync();

        var contents = _fixture.Resolver.Resolve().Contents;
        Assert.Equal("Real There", Assert.Single(contents).Winner!.Title);
        Assert.Single(_fixture.Registry.State.Stubs);
    }

    [Fact]
    public async Task AnItemThatBecomesAStubLeavesWithAnIncrementalRead()
    {
        var film = TestItems.Movie("m");
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, film);
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        Assert.Single(_fixture.Resolver.Resolve().Contents);

        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)][0] = WithPath(film, StubUrl);
        _fixture.Sync.Client.SavedUtc["m"] = SyncFixture.Start.AddMinutes(1);
        await _fixture.Sync.Sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Empty(_fixture.Resolver.Resolve().Contents);
    }

    [Fact]
    public void ThePathNeverReachesTheEntry()
    {
        var item = RemoteItemMapper.ToIndexedItem(WithPath(TestItems.Movie("m"), "http://friend.tailnet.ts.net/secret"), RemoteItemType.Movie, "films", DateTimeOffset.UnixEpoch);

        var json = System.Text.Json.JsonSerializer.Serialize(item);

        Assert.DoesNotContain("friend.tailnet", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AllStreamsAndChaptersReachTheEntryWithNoPath()
    {
        var film = TestItems.Movie("m") with
        {
            Chapters = [new RemoteChapter("Uno", 5)],
            MediaSources =
            [
                TestItems.Source("m", 1080, 1000) with
                {
                    MediaStreams =
                    [
                        new RemoteStreamInfo("Audio", "ac3", null, null, 192000, null, null, true) { Index = 3, Language = "ita", Channels = 6 }
                    ]
                }
            ]
        };

        var item = RemoteItemMapper.ToIndexedItem(film, RemoteItemType.Movie, "films", DateTimeOffset.UnixEpoch);

        var track = Assert.Single(Assert.Single(item.Versions).Streams);
        Assert.Equal((3, "ita", 6), (track.Index, track.Language, track.Channels));
        Assert.Equal(new IndexedChapter("Uno", 5), Assert.Single(item.Chapters));
    }
}
