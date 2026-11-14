using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Segments;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Playback;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Segments;

public class StubSegmentsTests
{
    private static readonly Guid _itemId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IStubRegistry> _stubs = new();
    private readonly Mock<IFederationIndex> _index = new();
    private readonly FakeRegistry _registry = new();
    private readonly FakeSegmentClient _client = new();
    private readonly ManualClock _clock = new();
    private readonly StubSource _best = PlaybackValues.Source("best");
    private readonly StubSource _backup = PlaybackValues.Source("backup");
    private readonly List<ServerCatalog> _catalogs = [];

    public StubSegmentsTests()
    {
        var video = new Movie { Id = _itemId, ShortcutPath = PlaybackRoutes.StrmUrl(8096, null, "stub", "signature") };
        _library.Setup(library => library.GetItemById(_itemId)).Returns(video);
        _stubs.Setup(stubs => stubs.Find("stub")).Returns(PlaybackValues.Stub(_best, _backup));
        _index.Setup(index => index.Snapshot).Returns(() => new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = IndexHealth.Ok, Servers = _catalogs.ToList() });
        Indexed(_best, 6_000, hasSegments: true);
        Indexed(_backup, 9_000, hasSegments: true);
    }

    private void Indexed(StubSource source, long? runTime, bool? hasSegments)
    {
        var remote = TestItems.Movie(source.ItemId) with
        {
            MediaSources = [TestItems.Source(source.MediaSourceId, 1080, 1000) with { RunTimeTicks = runTime, HasSegments = hasSegments }]
        };
        _catalogs.RemoveAll(catalog => catalog.ServerId == source.ServerId);
        _catalogs.Add(new ServerCatalog
        {
            ServerId = source.ServerId,
            State = ServerSyncState.Complete,
            Items = [RemoteItemMapper.ToIndexedItem(remote, RemoteItemType.Movie, "library", _clock.GetUtcNow())]
        });
    }

    private StubSegments Segments() => new(
        new StubLookup(_library.Object, _stubs.Object, new InMemoryConfigurationStore()),
        _index.Object,
        _registry,
        _client,
        new InMemoryConfigurationStore(),
        _clock,
        NullLogger<StubSegments>.Instance);

    private static MediaSegmentGenerationRequest Request(params MediaSegmentDto[] existing) => new() { ItemId = _itemId, ExistingSegments = existing };

    private static RemoteResult<RemoteSegmentRead> Found(IReadOnlyList<RemoteSegment> segments) => RemoteResult.Success(new RemoteSegmentRead(segments));

    private static RemoteResult<RemoteSegmentRead> Failed(RemoteFailure failure) => RemoteResult.Fail<RemoteSegmentRead>(failure);

    private static MediaSegmentDto Existing(MediaSegmentType type, long start, long end) => new() { Id = Guid.NewGuid(), ItemId = _itemId, Type = type, StartTicks = start, EndTicks = end };

    [Fact]
    public void Only_a_stub_is_supported()
    {
        var segments = Segments();

        Assert.True(segments.Supports(_itemId));
        Assert.False(segments.Supports(Guid.NewGuid()));
    }

    [Fact]
    public async Task The_segments_of_the_best_source_land_on_the_stub_and_their_origin_is_kept()
    {
        _client.Answer = _ => Found([new RemoteSegment(MediaSegmentType.Intro, 0, 900), new RemoteSegment(MediaSegmentType.Outro, 5000, 6000)]);
        var segments = Segments();

        var result = await segments.GetAsync(Request(), CancellationToken.None);

        Assert.Equal([(MediaSegmentType.Intro, 0L, 900L), (MediaSegmentType.Outro, 5000L, 6000L)], result.Select(segment => (segment.Type, segment.StartTicks, segment.EndTicks)));
        Assert.All(result, segment => Assert.Equal(_itemId, segment.ItemId));
        Assert.Equal(["best"], _client.Asked);
        Assert.Equal(new SegmentOrigin(_best, 6_000), segments.OriginOf(_itemId));
    }

    [Fact]
    public async Task Segments_are_cut_at_the_end_of_the_title()
    {
        _client.Answer = _ => Found(
        [
            new RemoteSegment(MediaSegmentType.Intro, 0, 900),
            new RemoteSegment(MediaSegmentType.Outro, 5000, 7000),
            new RemoteSegment(MediaSegmentType.Preview, 6000, 6500),
            new RemoteSegment(MediaSegmentType.Recap, 8000, 9000)
        ]);

        var result = await Segments().GetAsync(Request(), CancellationToken.None);

        Assert.Equal([(MediaSegmentType.Intro, 0L, 900L), (MediaSegmentType.Outro, 5000L, 6000L)], result.Select(segment => (segment.Type, segment.StartTicks, segment.EndTicks)));
    }

    [Fact]
    public async Task Segments_that_all_start_after_the_end_clear_the_stub()
    {
        _client.Answer = _ => Found([new RemoteSegment(MediaSegmentType.Outro, 6000, 7000)]);
        var segments = Segments();

        var result = await segments.GetAsync(Request(Existing(MediaSegmentType.Intro, 0, 900)), CancellationToken.None);

        Assert.Empty(result);
        Assert.Null(segments.OriginOf(_itemId));
    }

    [Fact]
    public async Task Segments_of_a_title_of_unknown_duration_are_kept_whole()
    {
        Indexed(_best, null, hasSegments: true);
        _client.Answer = _ => Found([new RemoteSegment(MediaSegmentType.Outro, 5000, 70_000)]);

        var result = await Segments().GetAsync(Request(), CancellationToken.None);

        Assert.Equal([(MediaSegmentType.Outro, 5000L, 70_000L)], result.Select(segment => (segment.Type, segment.StartTicks, segment.EndTicks)));
    }

    [Fact]
    public async Task A_best_source_the_remote_says_has_no_segments_is_not_asked()
    {
        Indexed(_best, 6_000, hasSegments: false);
        var segments = Segments();

        var result = await segments.GetAsync(Request(Existing(MediaSegmentType.Intro, 0, 900)), CancellationToken.None);

        Assert.Empty(result);
        Assert.Empty(_client.Asked);
        Assert.Null(segments.OriginOf(_itemId));
    }

    [Fact]
    public async Task A_best_source_whose_segments_are_unknown_is_asked()
    {
        Indexed(_best, 6_000, hasSegments: null);
        _client.Answer = _ => Found([new RemoteSegment(MediaSegmentType.Intro, 0, 900)]);

        var result = await Segments().GetAsync(Request(), CancellationToken.None);

        Assert.Single(result);
    }

    [Fact]
    public async Task No_segments_on_the_remote_clears_the_stub()
    {
        _client.Answer = _ => Found([new RemoteSegment(MediaSegmentType.Intro, 0, 900)]);
        var segments = Segments();
        await segments.GetAsync(Request(), CancellationToken.None);
        _client.Answer = _ => Found([]);

        var result = await segments.GetAsync(Request(Existing(MediaSegmentType.Intro, 0, 900)), CancellationToken.None);

        Assert.Empty(result);
        Assert.Null(segments.OriginOf(_itemId));
    }

    [Theory]
    [InlineData(RemoteFailure.Unreachable)]
    [InlineData(RemoteFailure.InvalidResponse)]
    public async Task A_failed_read_keeps_what_the_stub_has(RemoteFailure failure)
    {
        _client.Answer = _ => Failed(failure);
        var existing = Existing(MediaSegmentType.Intro, 0, 900);

        var result = await Segments().GetAsync(Request(existing), CancellationToken.None);

        Assert.Equal([existing], result);
    }

    [Fact]
    public async Task An_offline_server_yields_nothing_and_is_left_alone_for_a_while()
    {
        _registry.Offline.Add(_best.ServerId);
        var segments = Segments();

        Assert.Empty(await segments.GetAsync(Request(), CancellationToken.None));
        _registry.Offline.Clear();
        _client.Answer = _ => Found([new RemoteSegment(MediaSegmentType.Intro, 0, 900)]);
        Assert.Empty(await segments.GetAsync(Request(), CancellationToken.None));
        Assert.Empty(_client.Asked);

        _clock.Advance(StubSegments.OfflinePause);
        Assert.Single(await segments.GetAsync(Request(), CancellationToken.None));
    }

    [Theory]
    [InlineData(RemoteFailure.Forbidden)]
    [InlineData(RemoteFailure.ServerError)]
    [InlineData(RemoteFailure.InvalidResponse)]
    public async Task An_error_of_the_server_leaves_it_alone_for_a_while(RemoteFailure failure)
    {
        _client.Answer = _ => Failed(failure);
        var segments = Segments();
        await segments.GetAsync(Request(), CancellationToken.None);
        _client.Answer = _ => Found([new RemoteSegment(MediaSegmentType.Intro, 0, 900)]);

        Assert.Empty(await segments.GetAsync(Request(), CancellationToken.None));
        Assert.Single(_client.Asked);

        _clock.Advance(StubSegments.OfflinePause);
        Assert.Single(await segments.GetAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task An_item_the_remote_refuses_alone_keeps_what_the_stub_has_and_does_not_pause_its_server()
    {
        _client.Answer = _ => RemoteResult.Success(RemoteSegmentRead.Refused);
        var existing = Existing(MediaSegmentType.Intro, 0, 900);
        var segments = Segments();

        Assert.Equal([existing], await segments.GetAsync(Request(existing), CancellationToken.None));
        await segments.GetAsync(Request(), CancellationToken.None);

        Assert.Equal(2, _client.Asked.Count);
    }

    [Fact]
    public async Task A_busy_server_is_not_paused()
    {
        _client.Answer = _ => Failed(RemoteFailure.Busy);
        var segments = Segments();
        await segments.GetAsync(Request(), CancellationToken.None);

        await segments.GetAsync(Request(), CancellationToken.None);

        Assert.Equal(2, _client.Asked.Count);
    }

    [Fact]
    public async Task Access_denied_by_an_older_peer_means_no_segments_and_does_not_pause_it()
    {
        _registry.Servers.Add(new RemoteServerInfo(_best.ServerId, "peer", "http://peer", RemoteMode.JellyMesh, ProviderProtocol.SegmentsVersion - 1, [], null, null));
        _client.Answer = _ => Failed(RemoteFailure.Forbidden);
        var segments = Segments();

        Assert.Empty(await segments.GetAsync(Request(), CancellationToken.None));
        await segments.GetAsync(Request(), CancellationToken.None);

        Assert.Equal(2, _client.Asked.Count);
    }

    [Fact]
    public async Task Access_denied_by_a_peer_that_serves_segments_pauses_it()
    {
        _registry.Servers.Add(new RemoteServerInfo(_best.ServerId, "peer", "http://peer", RemoteMode.JellyMesh, ProviderProtocol.SegmentsVersion, [], null, null));
        _client.Answer = _ => Failed(RemoteFailure.Forbidden);
        var segments = Segments();
        await segments.GetAsync(Request(), CancellationToken.None);

        await segments.GetAsync(Request(), CancellationToken.None);

        Assert.Single(_client.Asked);
    }

    [Fact]
    public async Task A_slow_remote_is_a_failure_and_not_an_error()
    {
        _client.Answer = _ => throw new OperationCanceledException();
        var existing = Existing(MediaSegmentType.Outro, 10, 20);

        var result = await Segments().GetAsync(Request(existing), CancellationToken.None);

        Assert.Equal([existing], result);
    }

    [Fact]
    public async Task An_unexpected_error_keeps_what_the_stub_has_and_reaches_no_log_of_stock()
    {
        _client.Answer = _ => throw new System.Net.Http.HttpRequestException("http://friend.tailnet.ts.net:8096 refused");
        var existing = Existing(MediaSegmentType.Intro, 0, 900);

        var result = await Segments().GetAsync(Request(existing), CancellationToken.None);

        Assert.Equal([existing], result);
    }

    [Fact]
    public async Task A_cancelled_run_stops()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _client.Answer = _ => throw new OperationCanceledException(cancelled.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Segments().GetAsync(Request(), cancelled.Token));
    }

    [Fact]
    public async Task An_item_that_is_no_stub_gets_nothing()
    {
        var result = await Segments().GetAsync(new MediaSegmentGenerationRequest { ItemId = Guid.NewGuid(), ExistingSegments = [] }, CancellationToken.None);

        Assert.Empty(result);
        Assert.Empty(_client.Asked);
    }

    [Fact]
    public async Task Forgetting_an_item_drops_its_origin()
    {
        _client.Answer = _ => Found([new RemoteSegment(MediaSegmentType.Intro, 0, 900)]);
        var segments = Segments();
        await segments.GetAsync(Request(), CancellationToken.None);

        segments.Forget(_itemId);

        Assert.Null(segments.OriginOf(_itemId));
    }

    internal sealed class FakeSegmentClient : IRemoteSegmentClient
    {
        public List<string> Asked { get; } = [];

        public Func<string, RemoteResult<RemoteSegmentRead>> Answer { get; set; } = _ => Found([]);

        public Task<RemoteResult<RemoteSegmentRead>> GetSegmentsAsync(RemoteEndpoint endpoint, RemoteSession session, string itemId, CancellationToken cancellationToken)
        {
            Asked.Add(itemId);
            return Task.FromResult(Answer(itemId));
        }
    }
}
