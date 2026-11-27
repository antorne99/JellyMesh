using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Tests.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

/// <summary>Row 524: a stream that runs when the plugin database cannot be used goes on over the real stores.</summary>
public sealed class SuspendedSessionTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task While_the_database_cannot_be_used_the_bookkeeping_of_a_session_throws_nothing()
    {
        var fixture = new CoordinatorFixture();
        var store = new PluginConfigurationStore(_database, new FixedFederatedRootSource(null), NullLogger<PluginConfigurationStore>.Instance, fixture.Clock);
        using var lastServed = new LastServedSourceStore(_database, NullLogger<LastServedSourceStore>.Instance);
        var coordinator = new PlaybackCoordinator(fixture.Registry, fixture.Client, fixture.Sessions, store, lastServed, fixture.Clock, _database, NullLogger<PlaybackCoordinator>.Instance, fixture.Transcodes.Object);
        var request = new PlaybackOpenRequest { Identity = PlaybackValues.Identity(), Stub = PlaybackValues.Stub(PlaybackValues.Source()), LocalToken = PlaybackValues.LocalToken };
        await coordinator.OpenAsync(request, CancellationToken.None);
        var session = Assert.Single(fixture.Sessions.All);
        _database.Suspended = true;

        var segment = await coordinator.OpenAsync(session.Source.ServerId, new RemoteStreamRequest(HttpMethod.Get, "/videos/x/0.ts", null), CancellationToken.None);
        segment.Value!.Dispose();
        Assert.True(coordinator.StallLimit > TimeSpan.Zero);
        fixture.Sessions.ForgetUnusedLocal(store.Read(PlaybackTimes.From));
        await coordinator.SweepAsync();
        Assert.Same(session, Assert.Single(fixture.Sessions.All));
        coordinator.ReportFailure(session);
        await coordinator.CloseReportedAsync(session.Id, [session.Identity.UserId]);
        await coordinator.ReleaseAbandonedAsync();
        await coordinator.CloseFailedAsync();

        Assert.Empty(fixture.Sessions.All);
        Assert.Equal(PlaybackOutcomeKind.Unavailable, (await coordinator.OpenAsync(request, CancellationToken.None)).Kind);
    }
}
