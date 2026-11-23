using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Segments;
using Jellyfin.Plugin.JellyMesh.Tests.Playback;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Segments;

public class StubSegmentProviderTests
{
    [Fact]
    public async Task Building_the_provider_resolves_nothing_since_the_plugin_is_not_loaded_yet()
    {
        var services = new Mock<IServiceProvider>(MockBehavior.Strict);

        var provider = new StubSegmentProvider(services.Object);

        Assert.Equal("JellyMesh", provider.Name);
        await provider.CleanupExtractedData(Guid.NewGuid(), CancellationToken.None);
        services.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_provider_that_cannot_reach_its_worker_supports_nothing()
    {
        var services = new Mock<IServiceProvider>();
        services.Setup(provider => provider.GetService(typeof(StubSegments))).Throws(new InvalidOperationException("not initialized"));

        var provider = new StubSegmentProvider(services.Object);

        Assert.False(await provider.Supports(new Movie { Id = Guid.NewGuid() }));
    }

    [Theory]
    [InlineData(typeof(Audio))]
    [InlineData(typeof(Book))]
    [InlineData(typeof(Photo))]
    [InlineData(typeof(Folder))]
    public async Task An_item_that_is_no_video_is_not_supported_and_not_read_from_the_library(Type kind)
    {
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        var item = (BaseItem)Activator.CreateInstance(kind)!;
        item.Id = Guid.NewGuid();

        Assert.False(await Provider(library).Supports(item));
        library.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(typeof(Movie))]
    [InlineData(typeof(Episode))]
    [InlineData(typeof(MusicVideo))]
    public async Task A_video_stub_with_a_source_is_supported(Type kind)
    {
        var video = (Video)Activator.CreateInstance(kind)!;
        video.Id = Guid.NewGuid();
        video.ShortcutPath = PlaybackRoutes.StrmUrl(8096, null, "stub", "signature");
        var library = new Mock<ILibraryManager>();
        library.Setup(manager => manager.GetItemById(video.Id)).Returns(video);

        Assert.True(await Provider(library).Supports(video));
    }

    private static StubSegmentProvider Provider(Mock<ILibraryManager> library)
    {
        var stubs = new Mock<IStubRegistry>();
        stubs.Setup(registry => registry.Find("stub")).Returns(PlaybackValues.Stub(PlaybackValues.Source("best")));
        var worker = new StubSegments(
            new StubLookup(library.Object, stubs.Object, new InMemoryConfigurationStore()),
            new Mock<IFederationIndex>().Object,
            new FakeRegistry(),
            new Mock<IRemoteSegmentClient>().Object,
            new InMemoryConfigurationStore(),
            new ManualClock(),
            NullLogger<StubSegments>.Instance);
        var services = new Mock<IServiceProvider>();
        services.Setup(provider => provider.GetService(typeof(StubSegments))).Returns(worker);
        return new StubSegmentProvider(services.Object);
    }
}
