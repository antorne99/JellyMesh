using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

/// <summary>Real registry, reader, index and sync service over a fake remote and a temporary folder.</summary>
internal sealed class SyncFixture : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 11, 4, 3, 0, 0, TimeSpan.Zero);

    private readonly string _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jellymesh-tests-" + Guid.NewGuid().ToString("N"));

    public SyncFixture(string? key = TestValues.Key, string? stubFolder = null)
    {
        KeySource = new FixedKeySource(key);
        Vault = new CredentialVault(KeySource, Store);
        Registry = new RemoteServerRegistry(Store, Vault, Client, new CapturingLogger<RemoteServerRegistry>());
        Reader = new RemoteCatalogReader(Registry, Client, ReaderLogger);
        File = new IndexFile(_folder, new CapturingLogger<IndexFile>());
        Index = new FederationIndex(File, new CapturingLogger<FederationIndex>());
        Stubs = new StubRegistry(stubFolder ?? System.IO.Path.Combine(_folder, "plugin"), Store, new CapturingLogger<StubRegistry>());
        Sync = new CatalogSyncService(Registry, Reader, Index, Vault, Clock, Logger, stubs: Stubs);
    }

    public string Folder => _folder;

    public FakeRemoteClient Client { get; } = new();

    public InMemoryConfigurationStore Store { get; } = new();

    public FixedKeySource KeySource { get; }

    public CredentialVault Vault { get; }

    public RemoteServerRegistry Registry { get; }

    public CapturingLogger<RemoteCatalogReader> ReaderLogger { get; } = new();

    public RemoteCatalogReader Reader { get; }

    public IndexFile File { get; }

    public FederationIndex Index { get; }

    public FixedClock Clock { get; } = new(Start);

    public CapturingLogger<CatalogSyncService> Logger { get; } = new();

    public StubRegistry Stubs { get; }

    public CatalogSyncService Sync { get; }

    public async Task<RemoteServerInfo> AddServerAsync(string name, params string[] libraries)
    {
        var added = await Registry.AddAsync(name, TestValues.Url, TestValues.Credentials, CancellationToken.None);
        Assert.True(added.IsSuccess, added.Failure.ToString());
        Registry.SetImportedLibraries(added.Value!.Id, libraries);
        return Registry.GetServers().Single(server => server.Id == added.Value.Id);
    }

    public void Dispose()
    {
        Sync.Dispose();
        Stubs.Dispose();
        Index.Dispose();
        Registry.Dispose();
        if (System.IO.Directory.Exists(_folder))
        {
            System.IO.Directory.Delete(_folder, recursive: true);
        }
    }
}

internal sealed class FixedClock : TimeProvider
{
    public FixedClock(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}
