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
        Sync.Store.Configuration.FederatedFolderPath = Root;
        Registry = new StubRegistry(Path.Combine(_data, "plugin"), Sync.Store, new CapturingLogger<StubRegistry>());
        Resolver = new FederationResolver(Sync.Index, LocalCatalog, Sync.Registry);

        Network.Setup(configuration => configuration.GetConfiguration("network")).Returns(new NetworkConfiguration());
        Host.Setup(host => host.HttpPort).Returns(8096);

        Updater = new StubItemUpdater(Library.Object);
        Technical = new StubTechnicalData(Sync.Index, Streams.Object, Chapters.Object);
        Remover = new StubRemover(Library.Object);
        Materializer = new LibraryMaterializer(
            Registry,
            Resolver,
            Sync.Index,
            Sync.Registry,
            Sync.Client,
            Sync.Store,
            Library.Object,
            Updater,
            Technical,
            Remover,
            new StubUrlBuilder(Host.Object, Network.Object),
            Sync.Clock,
            Logger);
    }

    public SyncFixture Sync { get; } = new();

    public string Root { get; }

    public FakeCatalog LocalCatalog { get; } = new();

    public Mock<ILibraryManager> Library { get; } = new();

    public Mock<IMediaStreamRepository> Streams { get; } = new();

    public Mock<IChapterManager> Chapters { get; } = new();

    public Mock<IServerApplicationHost> Host { get; } = new();

    public Mock<IConfigurationManager> Network { get; } = new();

    public StubRegistry Registry { get; }

    public FederationResolver Resolver { get; }

    public StubItemUpdater Updater { get; }

    public StubTechnicalData Technical { get; }

    public StubRemover Remover { get; }

    public CapturingLogger<LibraryMaterializer> Logger { get; } = new();

    public LibraryMaterializer Materializer { get; }

    public string[] Files => Directory.Exists(Root)
        ? Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(Root, file)).Order(StringComparer.Ordinal).ToArray()
        : [];

    public static string Text(string path) => File.ReadAllText(path);

    public string Path_(params string[] parts) => Path.Combine(Root, Path.Combine(parts));

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

    internal sealed class FakeCatalog : ILocalCatalogReader
    {
        public List<CatalogItem> Items { get; } = [];

        public IReadOnlyList<CatalogItem> Read() => Items;
    }
}
