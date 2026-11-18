using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Tests.Storage;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

public sealed class LastServedSourceStoreTests : IDisposable
{
    private static readonly Guid _user = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _otherUser = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid _server = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _otherServer = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private LastServedSourceStore Store(CapturingLogger<LastServedSourceStore>? logger = null) => new(_database, logger ?? new CapturingLogger<LastServedSourceStore>());

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < 200 && !condition(); waited++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public void NothingIsKnown_InAnEmptyDatabase()
    {
        using var store = Store();

        Assert.Null(store.Find(_user, "stub"));
    }

    [Fact]
    public async Task ARecord_IsKnownAtOnce_AndWrittenRightAfter_WithoutAFlush()
    {
        using var store = Store();

        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);

        Assert.Equal(_server, store.Find(_user, "stub"));
        await WaitForAsync(() => _database.Count("last_served_sources") == 1);
        Assert.Equal(1, _database.Count("last_served_sources"));
        using var reloaded = Store();
        Assert.Equal(_server, reloaded.Find(_user, "stub"));
    }

    [Fact]
    public async Task AClosedStore_WritesWhatIsPending()
    {
        using (var store = Store())
        {
            await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        }

        Assert.Equal(1, _database.Count("last_served_sources"));
    }

    [Fact]
    public async Task AStoppingServer_WritesWhatIsPending()
    {
        using var store = Store();
        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);

        await store.StopAsync(CancellationToken.None);

        Assert.Equal(1, _database.Count("last_served_sources"));
    }

    [Fact]
    public async Task EachUserKeepsTheirOwnServer_PerStub()
    {
        using (var store = Store())
        {
            await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
            await store.RecordAsync(_otherUser, "stub", _otherServer, CancellationToken.None);
            await store.RecordAsync(_user, "other-stub", _otherServer, CancellationToken.None);
        }

        using var reloaded = Store();

        Assert.Equal(_server, reloaded.Find(_user, "stub"));
        Assert.Equal(_otherServer, reloaded.Find(_otherUser, "stub"));
        Assert.Equal(_otherServer, reloaded.Find(_user, "other-stub"));
        Assert.Null(reloaded.Find(_otherUser, "other-stub"));
        Assert.Equal(3, _database.Count("last_served_sources"));
    }

    [Fact]
    public async Task TheLatestServerReplacesTheOneBefore()
    {
        using (var store = Store())
        {
            await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
            await store.RecordAsync(_user, "stub", _otherServer, CancellationToken.None);
        }

        using var reloaded = Store();

        Assert.Equal(_otherServer, reloaded.Find(_user, "stub"));
        Assert.Equal(1, _database.Count("last_served_sources"));
    }

    [Fact]
    public async Task TheSameServerAgain_WritesNothing()
    {
        using var store = Store();
        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);
        var writes = _database.Writes;

        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);

        Assert.Equal(writes, _database.Writes);
    }

    [Fact]
    public async Task WhileTheDatabaseIsSuspended_ARecordIsKeptInMemory_Logged_AndWrittenOnceItCan()
    {
        using (var earlier = Store())
        {
            await earlier.RecordAsync(_otherUser, "stub", _otherServer, CancellationToken.None);
        }

        _database.Suspended = true;
        var logger = new CapturingLogger<LastServedSourceStore>();
        using var store = Store(logger);

        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);

        Assert.Equal(_server, store.Find(_user, "stub"));
        Assert.Null(store.Find(_otherUser, "stub"));
        Assert.NotEmpty(logger.Lines);

        _database.Suspended = false;
        await store.FlushAsync(CancellationToken.None);

        Assert.Equal(_otherServer, store.Find(_otherUser, "stub"));
        using var reloaded = Store();
        Assert.Equal(_server, reloaded.Find(_user, "stub"));
        Assert.Equal(_otherServer, reloaded.Find(_otherUser, "stub"));
    }

    [Fact]
    public async Task ARecordAfterARestart_KeepsTheOnesBefore()
    {
        using (var store = Store())
        {
            await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        }

        using (var restarted = Store())
        {
            await restarted.RecordAsync(_otherUser, "stub", _otherServer, CancellationToken.None);
        }

        using var reloaded = Store();

        Assert.Equal(_server, reloaded.Find(_user, "stub"));
        Assert.Equal(_otherServer, reloaded.Find(_otherUser, "stub"));
    }

    [Fact]
    public async Task APrune_DropsTheStubsThatAreGone_AndKeepsTheOthers()
    {
        using (var store = Store())
        {
            await store.RecordAsync(_user, "gone", _server, CancellationToken.None);
            await store.RecordAsync(_otherUser, "gone", _otherServer, CancellationToken.None);
            await store.RecordAsync(_user, "kept", _server, CancellationToken.None);

            var pruned = store.Prune(stubId => stubId == "kept", new HashSet<Guid>());

            Assert.Equal(2, pruned);
            Assert.Null(store.Find(_user, "gone"));
        }

        using var reloaded = Store();
        Assert.Null(reloaded.Find(_user, "gone"));
        Assert.Null(reloaded.Find(_otherUser, "gone"));
        Assert.Equal(_server, reloaded.Find(_user, "kept"));
        Assert.Equal(1, _database.Count("last_served_sources"));
    }

    [Fact]
    public async Task APrune_DropsTheServersRemoved_AlsoOnStubsThatStay()
    {
        using (var store = Store())
        {
            await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
            await store.RecordAsync(_otherUser, "stub", _otherServer, CancellationToken.None);

            store.Prune(_ => true, new HashSet<Guid> { _otherServer });
        }

        using var reloaded = Store();
        Assert.Equal(_server, reloaded.Find(_user, "stub"));
        Assert.Null(reloaded.Find(_otherUser, "stub"));
    }

    [Fact]
    public async Task APruneWithNothingToDrop_WritesNothing()
    {
        using var store = Store();
        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);
        var writes = _database.Writes;

        Assert.Equal(0, store.Prune(_ => true, new HashSet<Guid>()));
        await store.FlushAsync(CancellationToken.None);

        Assert.Equal(writes, _database.Writes);
    }
}
