using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Sync;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

// Q386: a sync skips its materialization and links only when nothing they read changed since the last completed run.
public sealed class UnchangedSyncGateTests : IDisposable
{
    private const string Root = "/federata";

    private readonly SyncFixture _fixture = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IManualMergeStore> _merges = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly List<Guid> _localIds = [Guid.NewGuid(), Guid.NewGuid()];
    private readonly List<VirtualFolderInfo> _folders = [new() { Name = "Film", ItemId = "f1", Locations = ["/media/film", Root + "/a/Film"] }];
    private readonly UnchangedSyncGate _gate;

    public UnchangedSyncGateTests()
    {
        _fixture.Store.Configuration.FederatedFolderPath = Root;
        _library.Setup(library => library.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns(() => [.. _localIds]);
        _library.Setup(library => library.GetVirtualFolders()).Returns(() => [.. _folders]);
        _merges.Setup(store => store.Merges).Returns([]);
        _host.Setup(host => host.HttpPort).Returns(8096);
        var network = new Mock<IConfigurationManager>();
        network.Setup(configuration => configuration.GetConfiguration("network")).Returns(new NetworkConfiguration());
        var signer = new StrmSigner(Path.Combine(_fixture.Folder, "signer"), new CapturingLogger<StrmSigner>());
        _gate = new UnchangedSyncGate(
            _fixture.Index,
            _fixture.Registry,
            _fixture.Store,
            _merges.Object,
            _library.Object,
            _fixture.Stubs,
            new StubUrlBuilder(_host.Object, network.Object, signer));
    }

    public static TheoryData<string> Changes => new()
    {
        "a local film appears",
        "a local film goes",
        "a library changes its folders",
        "a library is added",
        "a server's catalog changes",
        "a server is added",
        "a server's libraries change",
        "metadata protection changes",
        "collections change",
        "the federated folder changes",
        "the port of the stub URLs changes",
        "a merge is made",
        "the stub registry is saved",
        "an item is added",
        "a stub is removed",
        "a local film is updated",
        "the administrator splits the versions of a stub"
    };

    public void Dispose()
    {
        _gate.Dispose();
        _fixture.Dispose();
    }

    [Fact]
    public void NothingIsUnchangedBeforeARunIsRemembered()
    {
        Assert.False(_gate.IsUnchanged(_gate.Capture()));
    }

    [Fact]
    public void TheInputsOfTheLastRunAreUnchangedUntilForgotten()
    {
        _gate.Remember(_gate.Capture());
        Assert.True(_gate.IsUnchanged(_gate.Capture()));

        _gate.Forget();

        Assert.False(_gate.IsUnchanged(_gate.Capture()));
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task AnyChangeOfWhatTheRunReadsIsSeen(string change)
    {
        await _fixture.AddServerAsync("A", "films");
        await _fixture.Index.ReplaceServerAsync(_fixture.Registry.GetServers()[0].Id, [], SyncFixture.Start, null, CancellationToken.None);
        _gate.Remember(_gate.Capture());

        await ChangeAsync(change);

        Assert.False(_gate.IsUnchanged(_gate.Capture()));
    }

    [Fact]
    public void AStubThePluginOrAScanUpdatesOrAnItemThatIsNoTitleChangesNothing()
    {
        _gate.Remember(_gate.Capture());

        RaiseUpdated(new Movie { Path = Root + "/a/Film/A/A.strm" }, ItemUpdateType.MetadataImport);
        RaiseUpdated(new Movie { Path = Root + "/a/Film/A/A.strm" }, ItemUpdateType.ImageUpdate);
        RaiseAdded(new Person { Name = "Someone" });

        Assert.True(_gate.IsUnchanged(_gate.Capture()));
    }

    [Fact]
    public async Task ARunIsComparedWithTheRegistryAsItLeftIt()
    {
        var inputs = _gate.Capture();
        await _fixture.Stubs.SaveAsync(new StubState(), CancellationToken.None);

        _gate.Remember(inputs.WithRegistryGeneration(_fixture.Stubs.Generation));

        Assert.True(_gate.IsUnchanged(_gate.Capture()));
    }

    private async Task ChangeAsync(string change)
    {
        var server = _fixture.Registry.GetServers()[0];
        switch (change)
        {
            case "a local film appears":
                _localIds.Add(Guid.NewGuid());
                break;
            case "a local film goes":
                _localIds.RemoveAt(0);
                break;
            case "a library changes its folders":
                _folders[0] = new VirtualFolderInfo { Name = "Film", ItemId = "f1", Locations = ["/media/film"] };
                break;
            case "a library is added":
                _folders.Add(new VirtualFolderInfo { Name = "Serie", ItemId = "s1", Locations = [Root] });
                break;
            case "a server's catalog changes":
                IndexedItem film = new() { ItemId = "m1", Kind = IndexedItemKind.Movie, LibraryId = "films", LastSeenUtc = SyncFixture.Start };
                await _fixture.Index.ReplaceServerAsync(server.Id, [film], SyncFixture.Start, null, CancellationToken.None);
                break;
            case "a server is added":
                await _fixture.AddServerAsync("B", "films");
                break;
            case "a server's libraries change":
                _fixture.Registry.SetImportedLibraries(server.Id, ["films", "shows"]);
                break;
            case "metadata protection changes":
                _fixture.Store.Configuration.MetadataProtection = !_fixture.Store.Configuration.MetadataProtection;
                break;
            case "collections change":
                _fixture.Store.Configuration.FederateCollections = !_fixture.Store.Configuration.FederateCollections;
                break;
            case "the federated folder changes":
                _fixture.Store.Configuration.FederatedFolderPath = "/altra";
                break;
            case "the port of the stub URLs changes":
                _host.Setup(host => host.HttpPort).Returns(8097);
                break;
            case "a merge is made":
                _merges.Setup(store => store.Merges).Returns(new List<ManualMerge>());
                break;
            case "the stub registry is saved":
                await _fixture.Stubs.SaveAsync(_fixture.Stubs.State, CancellationToken.None);
                break;
            case "an item is added":
                RaiseAdded(new Movie { Path = "/media/film/B.mkv" });
                break;
            case "a stub is removed":
                RaiseRemoved(new Movie { Path = Root + "/a/Film/A/A.strm" });
                break;
            case "a local film is updated":
                RaiseUpdated(new Movie { Path = "/media/film/A.mkv" });
                break;
            case "the administrator splits the versions of a stub":
                RaiseUpdated(new Movie { Path = Root + "/a/Film/A/A.strm" }, ItemUpdateType.MetadataEdit);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, null);
        }
    }

    private void RaiseAdded(BaseItem item) => _library.Raise(library => library.ItemAdded += null, _library.Object, new ItemChangeEventArgs { Item = item });

    private void RaiseRemoved(BaseItem item) => _library.Raise(library => library.ItemRemoved += null, _library.Object, new ItemChangeEventArgs { Item = item });

    private void RaiseUpdated(BaseItem item, ItemUpdateType reason = ItemUpdateType.MetadataImport)
        => _library.Raise(library => library.ItemUpdated += null, _library.Object, new ItemChangeEventArgs { Item = item, UpdateReason = reason });
}
