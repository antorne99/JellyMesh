using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Segments;
using Jellyfin.Plugin.JellyMesh.Tests.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Segments;

public sealed class NewStubSegmentsTests : IAsyncLifetime, IDisposable
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(10);

    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IStubRegistry> _stubs = new();
    private readonly Mock<IMediaSegmentManager> _manager = new();
    private readonly LibraryOptions _options = new();
    private readonly ConcurrentQueue<Guid> _computed = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _done = new();
    private readonly NewStubSegments _service;

    public NewStubSegmentsTests()
    {
        _stubs.Setup(stubs => stubs.Find("stub")).Returns(PlaybackValues.Stub(PlaybackValues.Source()));
        _stubs.Setup(stubs => stubs.FindByPath(It.Is<string?>(path => path != null && path.StartsWith("/federated/", StringComparison.Ordinal)))).Returns(PlaybackValues.Stub(PlaybackValues.Source()));
        _library.Setup(library => library.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(_options);
        _manager.Setup(manager => manager.RunSegmentPluginProviders(It.IsAny<BaseItem>(), _options, false, It.IsAny<CancellationToken>()))
            .Returns<BaseItem, LibraryOptions, bool, CancellationToken>((item, _, _, _) =>
            {
                _computed.Enqueue(item.Id);
                _done.GetOrAdd(item.Id, _ => new TaskCompletionSource()).TrySetResult();
                return Task.CompletedTask;
            });
        var store = new InMemoryConfigurationStore();
        var segments = new StubSegments(
            new StubLookup(_library.Object, _stubs.Object, store),
            new Mock<IFederationIndex>().Object,
            new FakeRegistry(),
            new StubSegmentsTests.FakeSegmentClient(),
            store,
            TimeProvider.System,
            NullLogger<StubSegments>.Instance);
        _service = new NewStubSegments(_library.Object, _manager.Object, _stubs.Object, segments, NullLogger<NewStubSegments>.Instance);
    }

    public Task InitializeAsync() => _service.StartAsync(CancellationToken.None);

    public Task DisposeAsync() => _service.StopAsync(CancellationToken.None);

    public void Dispose() => _service.Dispose();

    private Movie Item(string path, bool read = true)
    {
        var item = new Movie { Id = Guid.NewGuid(), Path = path, ShortcutPath = read ? PlaybackRoutes.StrmUrl(8096, null, "stub", "signature") : null };
        _library.Setup(library => library.GetItemById(item.Id)).Returns(() => item);
        return item;
    }

    private void Added(BaseItem item) => _library.Raise(library => library.ItemAdded += null, _library.Object, new ItemChangeEventArgs { Item = item });

    private void Updated(BaseItem item) => _library.Raise(library => library.ItemUpdated += null, _library.Object, new ItemChangeEventArgs { Item = item });

    // Waits until the segments of an item were computed; the queue runs in order, so earlier items are done too.
    private Task ComputedAsync(Guid itemId) => _done.GetOrAdd(itemId, _ => new TaskCompletionSource()).Task.WaitAsync(_wait);

    private async Task SettleAsync()
    {
        var marker = Item("/federated/Films/Marker.strm");
        Added(marker);
        Updated(marker);
        await ComputedAsync(marker.Id);
    }

    [Fact]
    public async Task A_new_stub_gets_its_segments_once_its_refresh_read_the_strm()
    {
        var stub = Item("/federated/Films/New.strm");

        Added(stub);
        Updated(stub);
        await ComputedAsync(stub.Id);
        Updated(stub);
        await SettleAsync();

        Assert.Single(_computed, id => id == stub.Id);
    }

    [Fact]
    public async Task A_stub_whose_strm_is_not_read_yet_waits_for_the_refresh_that_reads_it()
    {
        var stub = Item("/federated/Films/Slow.strm", read: false);
        Added(stub);
        Updated(stub);
        await SettleAsync();
        Assert.DoesNotContain(stub.Id, _computed);

        stub.ShortcutPath = PlaybackRoutes.StrmUrl(8096, null, "stub", "signature");
        Updated(stub);

        await ComputedAsync(stub.Id);
    }

    [Fact]
    public async Task A_stub_that_was_only_updated_is_left_to_the_scheduled_scan()
    {
        var stub = Item("/federated/Films/Old.strm");

        Updated(stub);
        await SettleAsync();

        Assert.DoesNotContain(stub.Id, _computed);
    }

    [Fact]
    public async Task An_item_that_is_no_stub_is_left_alone()
    {
        var local = Item("/media/Films/Local.strm");

        Added(local);
        Updated(local);
        await SettleAsync();

        Assert.DoesNotContain(local.Id, _computed);
    }

    [Fact]
    public async Task A_failure_of_one_stub_does_not_stop_the_others()
    {
        var broken = Item("/federated/Films/Broken.strm");
        _manager.Setup(manager => manager.RunSegmentPluginProviders(It.Is<BaseItem>(item => item.Id == broken.Id), _options, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("http://friend.tailnet.ts.net:8096"));

        Added(broken);
        Updated(broken);
        await SettleAsync();

        Assert.DoesNotContain(broken.Id, _computed);
    }
}
