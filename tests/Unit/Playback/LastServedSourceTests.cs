using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

/// <summary>"Ultima fonte" of the spec (D15): what the coordinator records after an open, per user and stub.</summary>
public sealed class LastServedSourceTests
{
    private static readonly PlaybackIdentity _user = PlaybackValues.Identity();
    private static readonly PlaybackIdentity _otherUser = PlaybackValues.Identity() with { UserId = Guid.Parse("33333333-3333-3333-3333-333333333333"), DeviceId = "other" };

    private static PlaybackOpenRequest Request(PlaybackIdentity identity, StubEntry stub) => new()
    {
        Identity = identity,
        Stub = stub,
        LocalToken = PlaybackValues.LocalToken
    };

    [Fact]
    public async Task A_remote_that_serves_is_recorded_for_the_user_and_the_stub()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();

        await fixture.OpenAsync(PlaybackValues.Stub(source));

        Assert.Equal(source.ServerId, fixture.LastServed.Find(_user.UserId, "stub"));
    }

    [Fact]
    public async Task The_backup_that_served_is_recorded_not_the_winner()
    {
        var fixture = new CoordinatorFixture();
        var winner = PlaybackValues.Source("winner");
        var backup = PlaybackValues.Source("backup");
        fixture.Registry.Offline.Add(winner.ServerId);

        await fixture.OpenAsync(PlaybackValues.Stub(winner, backup));

        Assert.Equal(backup.ServerId, fixture.LastServed.Find(_user.UserId, "stub"));
    }

    [Fact]
    public async Task The_server_whose_file_goes_to_the_local_transcode_is_recorded()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();
        fixture.Client.Answer = (request, _) =>
        {
            var answer = FakePlaybackClient.Hls(request);
            answer.Value!.MediaSources[0].TranscodingUrl = null;
            return Task.FromResult(answer);
        };

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(source));

        Assert.Equal(PlaybackOutcomeKind.LocalTranscode, outcome.Kind);
        Assert.Equal(source.ServerId, fixture.LastServed.Find(_user.UserId, "stub"));
    }

    [Fact]
    public async Task Nothing_is_recorded_when_no_server_serves()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();
        fixture.Registry.Offline.Add(source.ServerId);

        var outcome = await fixture.OpenAsync(PlaybackValues.Stub(source));

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
        Assert.Null(fixture.LastServed.Find(_user.UserId, "stub"));
    }

    [Fact]
    public async Task Nothing_is_recorded_when_the_client_left_before_the_answer()
    {
        var fixture = new CoordinatorFixture();
        var source = PlaybackValues.Source();
        using var leaving = new CancellationTokenSource();
        fixture.Client.Open = async (_, _) =>
        {
            await leaving.CancelAsync();
            return FakePlaybackClient.Bytes();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Coordinator.OpenAsync(Request(_user, PlaybackValues.Stub(source)), leaving.Token));

        Assert.Null(fixture.LastServed.Find(_user.UserId, "stub"));
    }

    [Fact]
    public async Task A_second_user_has_their_own_server_on_the_same_stub()
    {
        var fixture = new CoordinatorFixture();
        var winner = PlaybackValues.Source("winner");
        var backup = PlaybackValues.Source("backup");
        var stub = PlaybackValues.Stub(winner, backup);
        fixture.Registry.Offline.Add(winner.ServerId);
        await fixture.Coordinator.OpenAsync(Request(_user, stub), CancellationToken.None);
        fixture.Registry.Offline.Clear();

        await fixture.Coordinator.OpenAsync(Request(_otherUser, stub), CancellationToken.None);

        Assert.Equal(backup.ServerId, fixture.LastServed.Find(_user.UserId, "stub"));
        Assert.Equal(winner.ServerId, fixture.LastServed.Find(_otherUser.UserId, "stub"));
    }

    [Fact]
    public async Task The_record_on_disk_survives_a_restart_and_holds_no_address_and_no_token()
    {
        var folder = Path.Combine(Path.GetTempPath(), "jellymesh-last-served-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = PlaybackValues.Source("remote-item-sentinel");
            using (var store = new LastServedSourceStore(folder, NullLogger<LastServedSourceStore>.Instance))
            {
                var fixture = new CoordinatorFixture(store);
                await fixture.OpenAsync(PlaybackValues.Stub(source));
            }

            using var restarted = new LastServedSourceStore(folder, NullLogger<LastServedSourceStore>.Instance);
            var text = await File.ReadAllTextAsync(Path.Combine(folder, LastServedSourceStore.FileName));

            Assert.Equal(source.ServerId, restarted.Find(_user.UserId, "stub"));
            Assert.DoesNotContain(PlaybackValues.RemoteToken, text, StringComparison.Ordinal);
            Assert.DoesNotContain(PlaybackValues.LocalToken, text, StringComparison.Ordinal);
            Assert.DoesNotContain(PlaybackValues.RemotePlaySession, text, StringComparison.Ordinal);
            Assert.DoesNotContain(PlaybackValues.RemoteDevice, text, StringComparison.Ordinal);
            Assert.DoesNotContain("tailnet", text, StringComparison.Ordinal);
            Assert.DoesNotContain("remote-item-sentinel", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }
}

/// <summary>A record of the last server that served, kept in memory.</summary>
internal sealed class InMemoryLastServedStore : ILastServedSourceStore
{
    private readonly ConcurrentDictionary<(Guid UserId, string StubId), Guid> _servers = new();

    public Guid? Find(Guid userId, string stubId) => _servers.TryGetValue((userId, stubId), out var serverId) ? serverId : null;

    public Task RecordAsync(Guid userId, string stubId, Guid serverId, CancellationToken cancellationToken)
    {
        _servers[(userId, stubId)] = serverId;
        return Task.CompletedTask;
    }
}
