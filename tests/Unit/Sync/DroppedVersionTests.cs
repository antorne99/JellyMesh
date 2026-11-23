using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Tests.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

/// <summary>A version a peer no longer sends leaves with the next full sync, with the item otherwise unchanged.</summary>
public sealed class DroppedVersionTests : IDisposable
{
    private readonly StubFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static RemoteMediaSource Version(string id, int height) => TestItems.Source(id, height, 5_000_000) with
    {
        MediaStreams = [new RemoteStreamInfo("Video", "h264", height, height * 16 / 9, 5_000_000, "SDR", "SDR", true) { Index = 0 }]
    };

    private static RemoteItem Film(params RemoteMediaSource[] sources) => TestItems.Movie("film") with { Etag = "same", MediaSources = sources };

    private async Task<RemoteServerInfo> SyncAsync(RemoteItem film)
    {
        _fixture.Sync.Client.Capabilities = new RemoteCapabilities(RemoteMode.JellyMesh, 1);
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, film, TestItems.Movie("other", "Other", 2021, "2"));
        var server = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        return server;
    }

    private async Task SendAsync(RemoteItem film)
    {
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)][0] = film;
        await _fixture.SyncAndMaterializeAsync();
        Assert.All(_fixture.Sync.Client.Queries, query => Assert.True(query.ViaProvider));
    }

    private string[] IndexedVersions(RemoteServerInfo server) =>
        _fixture.Sync.Index.Snapshot.Find(server.Id)!.Items.Single(item => item.ItemId == "film").Versions.Select(version => version.MediaSourceId).ToArray();

    private ResolvedContent? Content() => _fixture.Resolver.Resolve().Contents.SingleOrDefault(content => content.Winner?.ItemId == "film");

    private string[] StubVersions() => _fixture.Registry.State.Stubs
        .SelectMany(stub => stub.Sources)
        .Where(source => source.ItemId == "film")
        .Select(source => source.MediaSourceId)
        .ToArray();

    [Theory]
    [InlineData(2160, "dropped")]
    [InlineData(480, "own")]
    public async Task ItLeavesTheIndexAndTheSourcesOfTheStub(int height, string winner)
    {
        var own = Version("own", 1080);
        var server = await SyncAsync(Film(own, Version("dropped", height)));
        Assert.Equal(["own", "dropped"], IndexedVersions(server));
        Assert.Equal(winner, Content()!.Winner!.MediaSourceId);
        Assert.Equal(2, StubVersions().Length);
        var files = _fixture.Files;

        await SendAsync(Film(own));

        Assert.Equal(["own"], IndexedVersions(server));
        Assert.Equal("own", Content()!.Winner!.MediaSourceId);
        Assert.Empty(Content()!.Backups);
        Assert.Equal(["own"], StubVersions());
        Assert.Equal(files, _fixture.Files);
        Assert.DoesNotContain(_fixture.Files, file => StubFixture.Text(Path.Combine(_fixture.Root, file)).Contains("dropped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ItsStreamsLeaveTheIndexedItem()
    {
        var own = Version("own", 1080);
        var server = await SyncAsync(Film(own, Version("dropped", 2160)));
        var path = _fixture.Path_(server, "Film", "Film (2020)", "Film (2020) - A.strm");
        var item = new Movie { Id = Guid.NewGuid(), Path = path, Name = "Film" };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        IReadOnlyList<MediaStream> saved = [];
        _fixture.Streams.Setup(streams => streams.SaveMediaStreams(item.Id, It.IsAny<IReadOnlyList<MediaStream>>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, IReadOnlyList<MediaStream> streams, CancellationToken _) => saved = streams);

        await SendAsync(Film(own));

        Assert.Equal(1080, Assert.Single(saved, stream => stream.Type == MediaStreamType.Video).Height);
    }

    [Fact]
    public async Task ATitleLeftWithNoVersionLosesItsFiles()
    {
        var server = await SyncAsync(Film(Version("dropped", 1080)));
        var folder = _fixture.Path_(server, "Film", "Film (2020)");
        Assert.True(Directory.Exists(folder));

        await SendAsync(Film());

        Assert.Empty(IndexedVersions(server));
        Assert.Null(Content());
        Assert.Empty(StubVersions());
        Assert.False(Directory.Exists(folder));
        Assert.Single(_fixture.Registry.State.Stubs);
    }
}
