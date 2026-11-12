using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Activity;

public class SyncActivityTests
{
    private const string Completed = "JellyMeshSyncCompleted";
    private const string Failed = "JellyMeshSyncFailed";

    internal static CatalogSyncService NewSync(SyncFixture fixture, RecordingActivityWriter activity)
    {
        return new CatalogSyncService(
            fixture.Registry,
            fixture.Reader,
            fixture.Index,
            fixture.Vault,
            fixture.Clock,
            new CapturingLogger<CatalogSyncService>(),
            stubs: fixture.Stubs,
            activity: activity);
    }

    [Fact]
    public async Task Each_completed_sync_writes_one_entry()
    {
        using var fixture = new SyncFixture();
        await fixture.AddServerAsync("Friend", "films");
        var activity = new RecordingActivityWriter();
        using var sync = NewSync(fixture, activity);

        await sync.RunIncrementalAsync(CancellationToken.None);
        await sync.RunAsync(CancellationToken.None);

        Assert.Equal([Completed, Completed], activity.Entries.Select(entry => entry.Type));
        Assert.All(activity.Entries, entry => Assert.Equal(LogLevel.Information, entry.Severity));
        Assert.Equal("1 of 1 servers read", activity.Entries[0].ShortOverview);
    }

    [Fact]
    public async Task A_failing_server_is_written_once_and_named_in_each_completed_sync()
    {
        using var fixture = new SyncFixture();
        await fixture.AddServerAsync("Friend", "films");
        await fixture.AddServerAsync("Other", "films");
        var activity = new RecordingActivityWriter();
        using var sync = NewSync(fixture, activity);
        fixture.Client.ClockFailure = RemoteFailure.Unreachable;

        await sync.RunIncrementalAsync(CancellationToken.None);
        await sync.RunIncrementalAsync(CancellationToken.None);

        var failed = activity.OfType(Failed);
        Assert.Equal(2, failed.Count);
        Assert.Contains(failed, entry => entry.Name.Contains("Friend", StringComparison.Ordinal));
        Assert.Contains(failed, entry => entry.Name.Contains("Other", StringComparison.Ordinal));
        Assert.All(failed, entry => Assert.Equal("Unreachable", entry.ShortOverview));

        var completed = activity.OfType(Completed);
        Assert.Equal(2, completed.Count);
        Assert.All(completed, entry =>
        {
            Assert.Equal(LogLevel.Warning, entry.Severity);
            Assert.Equal("0 of 2 servers read", entry.ShortOverview);
            Assert.Contains("Friend (Unreachable)", entry.Overview, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_server_that_fails_again_after_a_good_sync_is_written_again()
    {
        using var fixture = new SyncFixture();
        await fixture.AddServerAsync("Friend", "films");
        var activity = new RecordingActivityWriter();
        using var sync = NewSync(fixture, activity);

        fixture.Client.ClockFailure = RemoteFailure.Timeout;
        await sync.RunIncrementalAsync(CancellationToken.None);
        fixture.Client.ClockFailure = RemoteFailure.None;
        await sync.RunIncrementalAsync(CancellationToken.None);
        fixture.Client.ClockFailure = RemoteFailure.ServerError;
        await sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(["Timeout", "ServerError"], activity.OfType(Failed).Select(entry => entry.ShortOverview));
    }

    [Fact]
    public async Task A_failure_stored_before_a_restart_is_not_written_again()
    {
        using var fixture = new SyncFixture();
        await fixture.AddServerAsync("Friend", "films");
        fixture.Client.ClockFailure = RemoteFailure.Unreachable;
        using (var before = NewSync(fixture, new RecordingActivityWriter()))
        {
            await before.RunIncrementalAsync(CancellationToken.None);
        }

        var activity = new RecordingActivityWriter();
        using var after = NewSync(fixture, activity);
        await after.RunIncrementalAsync(CancellationToken.None);

        Assert.Empty(activity.OfType(Failed));
    }

    [Fact]
    public async Task A_sync_that_does_not_run_writes_nothing()
    {
        using var fixture = new SyncFixture(key: null);
        var activity = new RecordingActivityWriter();
        using var sync = NewSync(fixture, activity);

        Assert.Equal(SyncStartResult.Suspended, await sync.RunIncrementalAsync(CancellationToken.None));

        Assert.Empty(activity.Entries);
    }

    [Fact]
    public async Task No_entry_holds_the_address_or_the_token()
    {
        using var fixture = new SyncFixture();
        await fixture.AddServerAsync("Friend", "films");
        var activity = new RecordingActivityWriter();
        using var sync = NewSync(fixture, activity);

        fixture.Client.ClockFailure = RemoteFailure.Unreachable;
        await sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(2, activity.Entries.Count);
        foreach (var entry in activity.Entries)
        {
            var text = string.Join(' ', entry.Name, entry.ShortOverview, entry.Overview);
            Assert.DoesNotContain(TestValues.Host, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(TestValues.Token, text, StringComparison.Ordinal);
            Assert.DoesNotContain(TestValues.Password, text, StringComparison.Ordinal);
        }
    }
}
