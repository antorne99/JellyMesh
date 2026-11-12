using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Moq;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

/// <summary>Real index, resolver, registry and folder over a fake remote and a mocked library.</summary>
internal sealed class StubFixture : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "jellymesh-stubs-" + Guid.NewGuid().ToString("N"));

    public StubFixture()
    {
        Root = Path.Combine(_data, "federata");
        Sync = new SyncFixture(stubFolder: Path.Combine(_data, "plugin"));
        Sync.Store.Configuration.FederatedFolderPath = Root;
        Registry = Sync.Stubs;
        Merges = new ManualMergeStore(Path.Combine(_data, "plugin"), new CapturingLogger<ManualMergeStore>());
        Resolver = new FederationResolver(Sync.Index, LocalCatalog, Sync.Registry, Merges);
        Hooked = new HookedResolver(Resolver, () => AfterResolve?.Invoke());

        Network.Setup(configuration => configuration.GetConfiguration("network")).Returns(new NetworkConfiguration());
        Host.Setup(host => host.HttpPort).Returns(8096);

        Signer = new StrmSigner(Path.Combine(_data, "plugin"), new CapturingLogger<StrmSigner>());
        Updater = new StubItemUpdater(Library.Object);
        Technical = new StubTechnicalData(Sync.Index, Streams.Object, Chapters.Object);
        Remover = new StubRemover(Library.Object);
        Materializer = new LibraryMaterializer(
            Registry,
            Hooked,
            Sync.Index,
            Sync.Registry,
            Sync.Client,
            Sync.Store,
            Library.Object,
            Updater,
            Technical,
            Remover,
            new StubUrlBuilder(Host.Object, Network.Object, Signer),
            Sync.Clock,
            Logger);
    }

    public SyncFixture Sync { get; }

    public string Root { get; }

    public FakeCatalog LocalCatalog { get; } = new();

    public Mock<ILibraryManager> Library { get; } = new();

    public Mock<IMediaStreamRepository> Streams { get; } = new();

    public Mock<IChapterManager> Chapters { get; } = new();

    public Mock<IServerApplicationHost> Host { get; } = new();

    public Mock<IConfigurationManager> Network { get; } = new();

    public StubRegistry Registry { get; }

    public StrmSigner Signer { get; }

    public ManualMergeStore Merges { get; }

    public FederationResolver Resolver { get; }

    /// <summary>Gets the resolver the materializer calls.</summary>
    public HookedResolver Hooked { get; }

    /// <summary>Gets or sets what happens right after the materializer resolves the catalog.</summary>
    public Action? AfterResolve { get; set; }

    public StubItemUpdater Updater { get; }

    public StubTechnicalData Technical { get; }

    public StubRemover Remover { get; }

    public CapturingLogger<LibraryMaterializer> Logger { get; } = new();

    public LibraryMaterializer Materializer { get; }

    public string[] Files => Directory.Exists(Root)
        ? Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(Root, file)).Order(StringComparer.Ordinal).ToArray()
        : [];

    public static string Text(string path) => File.ReadAllText(path);

    /// <summary>Builds a path relative to the root, inside the folder of a server.</summary>
    public static string In(RemoteServerInfo server, params string[] parts) => Path.Combine(FederatedPaths.ServerFolder(server.Id), Path.Combine(parts));

    public string Path_(params string[] parts) => Path.Combine(Root, Path.Combine(parts));

    /// <summary>Builds an absolute path inside the folder of a server.</summary>
    public string Path_(RemoteServerInfo server, params string[] parts) => Path.Combine(Root, In(server, parts));

    public async Task SyncAndMaterializeAsync()
    {
        await Sync.Sync.RunAsync(CancellationToken.None);
        await Materializer.MaterializeAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        Registry.Dispose();
        Sync.Dispose();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, true);
        }
    }

    internal sealed class HookedResolver(IFederationResolver inner, Action after) : IFederationResolver
    {
        /// <summary>Gets the servers of the last call, or null if it read them by itself.</summary>
        public IReadOnlyList<RemoteServerInfo>? LastServers { get; private set; }

        public FederationView Resolve()
        {
            LastServers = null;
            var view = inner.Resolve();
            after();
            return view;
        }

        public FederationView Resolve(IndexSnapshot snapshot, IReadOnlyList<RemoteServerInfo> servers)
        {
            LastServers = servers;
            var view = inner.Resolve(snapshot, servers);
            after();
            return view;
        }
    }

    internal sealed class FakeCatalog : ILocalCatalogReader
    {
        public List<CatalogItem> Items { get; } = [];

        public IReadOnlyList<CatalogItem> Read() => Items;
    }
}
