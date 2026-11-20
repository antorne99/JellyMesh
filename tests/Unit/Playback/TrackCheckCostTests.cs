using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Tests.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

// Row 462: the checks the guard makes on 100 tracks stay under 50 ms with the registry of three servers of 100,000
// tracks. Jellyfin's own lookups are a dictionary here: only JellyMesh's work is measured.
public sealed class TrackCheckCostTests : IDisposable
{
    private const int Tracks = 100_000;
    private const int Servers = 3;
    private const int Checked = 100;
    private const int TracksPerAlbum = 10;

    private readonly string _data = Path.Combine(Path.GetTempPath(), "jellymesh-track-cost-" + Guid.NewGuid().ToString("N"));
    private readonly TestDatabase _database;
    private readonly InMemoryConfigurationStore _store = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly User _user = new("listener", "auth", "reset");
    private readonly StubRegistry _registry;
    private readonly ITestOutputHelper _output;

    public TrackCheckCostTests(ITestOutputHelper output)
    {
        _output = output;
        _store.Configuration.FederatedFolderPath = "/federata";
        _database = new TestDatabase(_data);
        _registry = new StubRegistry(_database, _store, new CapturingLogger<StubRegistry>());
        _library.Setup(library => library.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
        _library.Setup(library => library.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User>())).Returns((Guid id, User _) => _items.GetValueOrDefault(id));
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([new Folder()]);
    }

    public void Dispose()
    {
        _registry.Dispose();
        _database.Dispose();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, true);
        }
    }

    private static Guid ServerId(int server) => new(0, 0, (short)(server + 1), new byte[8]);

    private static Guid ItemId(int track, int server) => new(track, (short)server, 0, new byte[8]);

    private static string RelativePath(int track, int server)
    {
        var album = track / TracksPerAlbum;
        return Path.Combine(ServerId(server).ToString("D"), "Musica", "lib", $"Artist {album / 4:00000}", $"Album {album:000000}", $"{track % TracksPerAlbum + 1:00} - Song {track:000000}.strm");
    }

    private static StubEntry Stub(int track, int server) => new()
    {
        Id = $"t{track}s{server}",
        Kind = StubKind.Track,
        ServerId = ServerId(server),
        RelativePath = RelativePath(track, server),
        IdentityKey = "Track|key-" + track,
        Sources = [new StubSource(ServerId(server), $"item-{track}-{server}", $"item-{track}-{server}") { LibraryId = "lib" }]
    };

    // The registry of every track on every server, with no version groups as the planner writes for tracks, and the
    // items of the first tracks of each server.
    private async Task SeedAsync()
    {
        var stubs = new List<StubEntry>(Tracks * Servers);
        for (var server = 0; server < Servers; server++)
        {
            stubs.AddRange(Enumerable.Range(0, Tracks).Select(track => Stub(track, server)));
        }

        await _registry.EnterAsync(CancellationToken.None);
        try
        {
            await _registry.SaveAsync(new StubState { Stubs = stubs, Groups = [] }, CancellationToken.None);
        }
        finally
        {
            _registry.Exit();
        }

        for (var track = 0; track < Checked; track++)
        {
            var server = track % Servers;
            _items[ItemId(track, server)] = new Audio { Id = ItemId(track, server), Path = "/federata/" + RelativePath(track, server) };
        }
    }

    [Fact]
    public async Task TheChecksOf100TracksTakeLessThan50Ms()
    {
        await SeedAsync();
        var lookup = new StubLookup(_library.Object, _registry, _store);

        void CheckAll()
        {
            for (var track = 0; track < Checked; track++)
            {
                var item = ItemId(track, track % Servers);
                Assert.False(lookup.HasVersions(item));
                Assert.True(lookup.IsAudioStub(item));
                Assert.True(lookup.IsStub(item));
                Assert.True(lookup.Sees(_user, item));
                Assert.Equal($"t{track}s{track % Servers}", lookup.FindPlayable(item)?.Id);
            }
        }

        CheckAll();

        // The fastest of several runs: load on a shared host slows some runs, never the best one.
        var fastest = Enumerable.Range(0, 7)
            .Select(_ =>
            {
                var watch = Stopwatch.StartNew();
                CheckAll();
                return watch.Elapsed;
            })
            .Min();

        _output.WriteLine($"fastest of 7: {fastest.TotalMilliseconds:F2} ms");
        Assert.True(fastest < TimeSpan.FromMilliseconds(50), $"The checks of {Checked} tracks took {fastest.TotalMilliseconds:F1} ms.");
    }
}
