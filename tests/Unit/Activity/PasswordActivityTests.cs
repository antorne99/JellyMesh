using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Activity;

public class PasswordActivityTests
{
    private const string PasswordUnreadable = "JellyMeshPasswordUnreadable";

    private static CatalogSyncService NewSync(SyncFixture fixture, RecordingActivityWriter activity) => SyncActivityTests.NewSync(fixture, activity);

    [Fact]
    public async Task A_password_that_stops_decrypting_is_written_once_until_it_is_fixed()
    {
        using var fixture = new SyncFixture();
        await fixture.AddServerAsync("Friend", "films");
        var activity = new RecordingActivityWriter();
        using var sync = NewSync(fixture, activity);

        fixture.KeySource.Key = TestValues.OtherKey;
        Assert.Equal(SyncStartResult.Suspended, await sync.RunIncrementalAsync(CancellationToken.None));
        await sync.RunAsync(CancellationToken.None);

        var unreadable = Assert.Single(activity.OfType(PasswordUnreadable));
        Assert.Equal(LogLevel.Error, unreadable.Severity);
        Assert.Contains("Friend", unreadable.Name, StringComparison.Ordinal);

        fixture.KeySource.Key = TestValues.Key;
        await sync.RunIncrementalAsync(CancellationToken.None);
        fixture.KeySource.Key = TestValues.OtherKey;
        await sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(2, activity.OfType(PasswordUnreadable).Count);
    }

    [Fact]
    public async Task A_missing_key_is_not_reported_as_passwords_that_no_longer_decrypt()
    {
        using var fixture = new SyncFixture();
        await fixture.AddServerAsync("Friend", "films");
        var activity = new RecordingActivityWriter();
        using var sync = NewSync(fixture, activity);

        fixture.KeySource.Key = null;
        await sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Empty(activity.Entries);
    }

    [Fact]
    public async Task The_entry_names_the_server_and_nothing_secret()
    {
        using var fixture = new SyncFixture();
        await fixture.AddServerAsync("Friend", "films");
        var activity = new RecordingActivityWriter();
        using var sync = NewSync(fixture, activity);

        fixture.KeySource.Key = TestValues.OtherKey;
        await sync.RunIncrementalAsync(CancellationToken.None);

        var entry = Assert.Single(activity.Entries);
        var text = string.Join(' ', entry.Name, entry.ShortOverview, entry.Overview);
        Assert.DoesNotContain(TestValues.Host, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestValues.Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Key, text, StringComparison.Ordinal);
    }
}
