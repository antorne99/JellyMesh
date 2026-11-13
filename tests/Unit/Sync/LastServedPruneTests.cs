using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Playback;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

/// <summary>Q76: after each sync the record of the last server that served drops the stubs and the servers that are gone.</summary>
public sealed class LastServedPruneTests : IDisposable
{
    private static readonly Guid _user = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _removedServer = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private readonly SyncFixture _fixture = new();
    private readonly InMemoryLastServedStore _lastServed = new();
    private readonly CatalogSyncService _sync;

    public LastServedPruneTests()
    {
        _sync = new CatalogSyncService(
            _fixture.Registry,
            _fixture.Reader,
            _fixture.Index,
            _fixture.Vault,
            _fixture.Clock,
            _fixture.Logger,
            stubs: _fixture.Stubs,
            store: _fixture.Store,
            lastServed: _lastServed);
    }

    public void Dispose()
    {
        _sync.Dispose();
        _fixture.Dispose();
    }

    private static StubEntry Stub(string id, Guid server) => new()
    {
        Id = id,
        Kind = StubKind.Movie,
        ServerId = server,
        RelativePath = System.IO.Path.Combine(server.ToString("D"), "Film", id, id + ".strm"),
        IdentityKey = "key-" + id,
        Sources = [new StubSource(server, "item-" + id, "item-" + id)]
    };

    private async Task SeedAsync(StubState state)
    {
        await _fixture.Stubs.EnterAsync(CancellationToken.None);
        try
        {
            await _fixture.Stubs.SaveAsync(state, CancellationToken.None);
        }
        finally
        {
            _fixture.Stubs.Exit();
        }
    }

    [Fact]
    public async Task ASync_DropsTheStubsThatAreGone_AndTheServersRemoved()
    {
        var server = await _fixture.AddServerAsync("A");
        await SeedAsync(new StubState { Stubs = [Stub("kept", server.Id), Stub("on-removed", _removedServer)], RemovedServers = [_removedServer] });
        await _lastServed.RecordAsync(_user, "kept", server.Id, CancellationToken.None);
        await _lastServed.RecordAsync(_user, "gone", server.Id, CancellationToken.None);
        await _lastServed.RecordAsync(_user, "on-removed", _removedServer, CancellationToken.None);

        await _sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(server.Id, _lastServed.Find(_user, "kept"));
        Assert.Null(_lastServed.Find(_user, "gone"));
        Assert.Null(_lastServed.Find(_user, "on-removed"));
    }

    [Fact]
    public async Task ASyncWithAnEmptyRegistry_DropsNoStub()
    {
        var server = await _fixture.AddServerAsync("A");
        await _lastServed.RecordAsync(_user, "stub", server.Id, CancellationToken.None);

        await _sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(server.Id, _lastServed.Find(_user, "stub"));
    }

    [Fact]
    public async Task AServerMissingFromTheConfiguration_IsNotDroppedWithoutTheMarkOfARemoval()
    {
        var server = await _fixture.AddServerAsync("A");
        var missing = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        await SeedAsync(new StubState { Stubs = [Stub("kept", server.Id), Stub("missing", missing)] });
        await _lastServed.RecordAsync(_user, "missing", missing, CancellationToken.None);

        await _sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(missing, _lastServed.Find(_user, "missing"));
    }
}
