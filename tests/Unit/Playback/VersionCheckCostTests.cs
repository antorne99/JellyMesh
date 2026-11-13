using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;
using LinkedChildType = MediaBrowser.Controller.Entities.LinkedChildType;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

// Costo (Q27, Q36): the request filter and the candidate check of 100 items stay under 50 ms with the registry of three
// servers of 55,000 titles. Jellyfin's own lookups are a dictionary here: only JellyMesh's work is measured.
public sealed class VersionCheckCostTests : IDisposable
{
    private const int Titles = 55_000;
    private const int Servers = 3;
    private const int Checked = 100;

    private readonly string _data = Path.Combine(Path.GetTempPath(), "jellymesh-cost-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryConfigurationStore _store = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IFederationIndex> _index = new();
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly User _user = new("viewer", "auth", "reset");
    private readonly StubRegistry _registry;

    public VersionCheckCostTests()
    {
        _store.Configuration.FederatedFolderPath = "/federata";
        _registry = new StubRegistry(_data, _store, new CapturingLogger<StubRegistry>());
        _library.Setup(library => library.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
        _library.Setup(library => library.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User>())).Returns((Guid id, User _) => _items.GetValueOrDefault(id));
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([new Folder()]);
        _index.Setup(index => index.Snapshot).Returns(new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = IndexHealth.Ok, Servers = [] });
    }

    public void Dispose()
    {
        _registry.Dispose();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, true);
        }
    }

    private static string StubId(int title, int server) => $"t{title}s{server}";

    private static Guid ItemId(int title, int server) => new(title, (short)server, 0, new byte[8]);

    private static StubEntry Stub(int title, int server)
    {
        var id = StubId(title, server);
        var serverId = new Guid(0, 0, (short)(server + 1), new byte[8]);
        return new StubEntry
        {
            Id = id,
            Kind = StubKind.Movie,
            ServerId = serverId,
            RelativePath = Path.Combine(serverId.ToString("D"), "Film", id, id + ".strm"),
            IdentityKey = "key-" + title,
            Sources = [new StubSource(serverId, "item-" + id, "item-" + id)]
        };
    }

    // The registry of every title on every server, and the items of the first titles, linked as Jellyfin links versions.
    private async Task SeedAsync()
    {
        var stubs = new List<StubEntry>(Titles * Servers);
        var groups = new List<VersionGroup>(Titles);
        for (var title = 0; title < Titles; title++)
        {
            var members = Enumerable.Range(0, Servers).Select(server => Stub(title, server)).ToList();
            stubs.AddRange(members);
            groups.Add(new VersionGroup { Id = "g" + title, IdentityKey = "key-" + title, StubIds = members.Select(stub => stub.Id).ToList() });
        }

        await _registry.EnterAsync(CancellationToken.None);
        try
        {
            await _registry.SaveAsync(new StubState { Stubs = stubs, Groups = groups }, CancellationToken.None);
        }
        finally
        {
            _registry.Exit();
        }

        for (var title = 0; title < Checked; title++)
        {
            var videos = Enumerable.Range(0, Servers).Select(server =>
            {
                var stub = Stub(title, server);
                var video = new Movie { Id = ItemId(title, server), Path = "/federata/" + stub.RelativePath, ShortcutPath = PlaybackRoutes.StrmUrl(8096, null, stub.Id, "signature") };
                _items[video.Id] = video;
                return video;
            }).ToList();
            videos[0].LinkedAlternateVersions = videos.Skip(1).Select(video => new LinkedChild { ItemId = video.Id, Type = LinkedChildType.LinkedAlternateVersion }).ToArray();
            foreach (var alternate in videos.Skip(1))
            {
                alternate.SetPrimaryVersionId(videos[0].Id);
            }
        }
    }

    [Fact]
    public async Task TheChecksOf100ItemsTakeLessThan50Ms()
    {
        await SeedAsync();
        var lookup = new StubLookup(_library.Object, _registry, _store);
        var versions = new PlaybackVersions(lookup, _registry, _library.Object, _index.Object, _store);

        void CheckAll()
        {
            for (var title = 0; title < Checked; title++)
            {
                var item = ItemId(title, 0);
                Assert.True(lookup.HasVersions(item));
                Assert.True(lookup.Sees(_user, item));
                Assert.True(versions.IsVersionOf(item, ItemId(title, 1)));
                Assert.Equal(Servers - 1, versions.Fallbacks(item, _registry.Find(StubId(title, 0))!, _user).Count);
            }
        }

        CheckAll();
        var median = Enumerable.Range(0, 5)
            .Select(_ =>
            {
                var watch = Stopwatch.StartNew();
                CheckAll();
                return watch.Elapsed;
            })
            .Order()
            .ElementAt(2);

        Assert.True(median < TimeSpan.FromMilliseconds(50), $"The checks of {Checked} items took {median.TotalMilliseconds:F1} ms.");
    }
}
