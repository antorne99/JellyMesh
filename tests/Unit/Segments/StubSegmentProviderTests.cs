using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Segments;
using MediaBrowser.Controller.Entities.Movies;
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
}
