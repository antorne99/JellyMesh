using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Segments;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Playback;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Segments;

public class SegmentOfferTests
{
    private const long Hour = 36_000_000_000;
    private static readonly Guid _itemId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IStubRegistry> _stubs = new();
    private readonly Mock<IFederationIndex> _index = new();
    private readonly Mock<IMediaSegmentManager> _manager = new();
    private readonly PlaybackSessionStore _sessions = new();
    private readonly StubSource _best = PlaybackValues.Source("best");
    private readonly StubSource _backup = PlaybackValues.Source("backup");
    private readonly List<ServerCatalog> _catalogs = [];
    private readonly StubSegments _segments;

    public SegmentOfferTests()
    {
        _library.Setup(library => library.GetItemById(_itemId)).Returns(new Movie { Id = _itemId, ShortcutPath = PlaybackRoutes.StrmUrl(8096, null, "stub", "signature") });
        _stubs.Setup(stubs => stubs.Find("stub")).Returns(PlaybackValues.Stub(_best, _backup));
        _index.Setup(index => index.Snapshot).Returns(() => new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = IndexHealth.Ok, Servers = _catalogs.ToList() });
        _manager.Setup(manager => manager.HasSegments(_itemId)).Returns(true);
        Indexed(_best, 2 * Hour);
        _segments = new StubSegments(Lookup(), _index.Object, new FakeRegistry(), new StubSegmentsTests.FakeSegmentClient(), new InMemoryConfigurationStore(), new ManualClock(), NullLogger<StubSegments>.Instance);
    }

    private StubLookup Lookup() => new(_library.Object, _stubs.Object, new InMemoryConfigurationStore());

    private void Indexed(StubSource source, long? runTime)
    {
        var remote = TestItems.Movie(source.ItemId) with { MediaSources = [TestItems.Source(source.MediaSourceId, 1080, 1000) with { RunTimeTicks = runTime }] };
        _catalogs.RemoveAll(catalog => catalog.ServerId == source.ServerId);
        _catalogs.Add(new ServerCatalog
        {
            ServerId = source.ServerId,
            State = ServerSyncState.Complete,
            Items = [RemoteItemMapper.ToIndexedItem(remote, RemoteItemType.Movie, "library", DateTimeOffset.UnixEpoch)]
        });
    }

    private SegmentOffer Offer() => new(_manager.Object, _segments, Lookup(), _sessions);

    [Fact]
    public void An_item_without_segments_offers_none()
    {
        _manager.Setup(manager => manager.HasSegments(_itemId)).Returns(false);

        Assert.False(Offer().Offers(_itemId, _best));
    }

    [Fact]
    public void The_source_the_segments_came_from_offers_them()
    {
        _segments.Remember(_itemId, new SegmentOrigin(_best, 2 * Hour));

        Assert.True(Offer().Offers(_itemId, _best with { LibraryId = "other" }));
    }

    [Theory]
    [InlineData(2 * Hour, true)]
    [InlineData((2 * Hour) + (2 * Hour / 200), true)]
    [InlineData((2 * Hour) + (2 * Hour / 50), false)]
    [InlineData(Hour, false)]
    public void A_fallback_offers_them_only_with_the_same_duration(long backupRunTime, bool offered)
    {
        _segments.Remember(_itemId, new SegmentOrigin(_best, 2 * Hour));
        Indexed(_backup, backupRunTime);

        Assert.Equal(offered, Offer().Offers(_itemId, _backup));
    }

    [Fact]
    public void A_fallback_of_unknown_duration_offers_none()
    {
        _segments.Remember(_itemId, new SegmentOrigin(_best, 2 * Hour));
        Indexed(_backup, null);

        Assert.False(Offer().Offers(_itemId, _backup));
    }

    [Fact]
    public void Segments_from_a_source_of_unknown_duration_follow_no_fallback()
    {
        _segments.Remember(_itemId, new SegmentOrigin(_best, null));
        Indexed(_backup, 2 * Hour);

        Assert.False(Offer().Offers(_itemId, _backup));
    }

    [Fact]
    public void Without_a_known_origin_the_best_source_stands_for_it()
    {
        Indexed(_backup, Hour);

        Assert.True(Offer().Offers(_itemId, _best));
        Assert.False(Offer().Offers(_itemId, _backup));
    }

    [Fact]
    public void Segments_of_an_item_that_is_no_stub_are_left_as_they_are()
    {
        var other = Guid.NewGuid();
        _manager.Setup(manager => manager.HasSegments(other)).Returns(true);

        Assert.True(Offer().Offers(other, null));
    }

    [Fact]
    public void A_remote_answer_offers_segments_by_the_session_that_serves_it()
    {
        _segments.Remember(_itemId, new SegmentOrigin(_best, 2 * Hour));
        Indexed(_backup, Hour);
        var session = new PlaybackSession(PlaybackValues.Identity() with { ItemId = _itemId }, _backup, "remote", PlaybackStreamKind.Hls, "/master.m3u8", DateTimeOffset.UnixEpoch);
        _sessions.Add(session);
        var response = new PlaybackInfoResponse { PlaySessionId = session.Id, MediaSources = [new MediaSourceInfo { HasSegments = true }] };

        Offer().OfferOnRemote(response, session.Identity);

        Assert.False(response.MediaSources[0].HasSegments);
    }

    [Fact]
    public void A_remote_answer_from_the_origin_keeps_its_segments()
    {
        _segments.Remember(_itemId, new SegmentOrigin(_best, 2 * Hour));
        var session = new PlaybackSession(PlaybackValues.Identity() with { ItemId = _itemId }, _best, "remote", PlaybackStreamKind.Hls, "/master.m3u8", DateTimeOffset.UnixEpoch);
        _sessions.Add(session);
        var response = new PlaybackInfoResponse { PlaySessionId = session.Id, MediaSources = [new MediaSourceInfo { HasSegments = false }] };

        Offer().OfferOnRemote(response, null);

        Assert.True(response.MediaSources[0].HasSegments);
    }

    [Fact]
    public void A_remote_answer_without_a_known_session_offers_none()
    {
        var response = new PlaybackInfoResponse { PlaySessionId = "unknown", MediaSources = [new MediaSourceInfo { HasSegments = true }] };

        Offer().OfferOnRemote(response, null);

        Assert.False(response.MediaSources[0].HasSegments);
    }
}
