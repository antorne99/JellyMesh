using System;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class LocalRefreshRequesterTests
{
    private readonly Mock<IProviderManager> _providers = new();
    private readonly CapturingLogger<LocalRefreshRequester> _logger = new();
    private readonly InMemoryConfigurationStore _store = new();
    private readonly FixedClock _clock = new(SyncFixture.Start);

    private LocalRefreshRequester Requester() => new(_providers.Object, Mock.Of<IFileSystem>(), _store, _clock, _logger);

    private void VerifyQueued(Guid itemId, Times times) =>
        _providers.Verify(provider => provider.QueueRefresh(itemId, It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()), times);

    [Fact]
    public void AnItemIsRefreshedAsTheDashboardRefreshesOne()
    {
        var item = Guid.NewGuid();

        Requester().Request([item]);

        _providers.Verify(
            provider => provider.QueueRefresh(
                item,
                It.Is<MetadataRefreshOptions>(options =>
                    options.MetadataRefreshMode == MetadataRefreshMode.Default
                    && options.ImageRefreshMode == MetadataRefreshMode.Default
                    && !options.ReplaceAllMetadata
                    && !options.ReplaceAllImages
                    && !options.ForceSave
                    && !options.IsAutomated),
                RefreshPriority.High),
            Times.Once);
    }

    [Fact]
    public void AnItemIsAskedForOnlyOnce()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var requester = Requester();

        requester.Request([first]);
        requester.Request([first, second]);
        requester.Request([first, second]);

        VerifyQueued(first, Times.Once());
        VerifyQueued(second, Times.Once());
    }

    [Fact]
    public void NothingToAskForQueuesNothing()
    {
        Requester().Request([]);

        _providers.Verify(provider => provider.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()), Times.Never);
        Assert.Empty(_logger.Lines);
    }

    [Fact]
    public void TheRequestIsLogged()
    {
        Requester().Request([Guid.NewGuid(), Guid.NewGuid()]);

        Assert.Contains(_logger.Lines, line => line.StartsWith("Asked Jellyfin to read the metadata of 2 new local films or series", StringComparison.Ordinal));
    }

    [Fact]
    public void AnItemNotAskedForIsNotOverdue()
    {
        Assert.False(Requester().IsOverdue(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(899, false)]
    [InlineData(900, true)]
    [InlineData(5000, true)]
    public void ARequestIsOverdueAfterOneSyncInterval(int seconds, bool overdue)
    {
        var item = Guid.NewGuid();
        _store.Configuration.SyncIntervalSeconds = 900;
        var requester = Requester();
        requester.Request([item]);

        _clock.Now = SyncFixture.Start.AddSeconds(seconds);

        Assert.Equal(overdue, requester.IsOverdue(item));
    }

    [Fact]
    public void AnIntervalBelowTheMinimumCountsAsTheMinimum()
    {
        var item = Guid.NewGuid();
        _store.Configuration.SyncIntervalSeconds = 10;
        var requester = Requester();
        requester.Request([item]);

        _clock.Now = SyncFixture.Start.AddSeconds(299);
        Assert.False(requester.IsOverdue(item));
        _clock.Now = SyncFixture.Start.AddSeconds(300);
        Assert.True(requester.IsOverdue(item));
    }

    [Fact]
    public void AskingAgainDoesNotRestartTheWait()
    {
        var item = Guid.NewGuid();
        var requester = Requester();
        requester.Request([item]);

        _clock.Now = SyncFixture.Start.AddSeconds(600);
        requester.Request([item]);
        _clock.Now = SyncFixture.Start.AddSeconds(900);

        Assert.True(requester.IsOverdue(item));
    }
}
