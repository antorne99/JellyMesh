using System;
using System.IO;
using System.Linq;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Provider;

/// <summary>The federated folder reached by another name: a symbolic link or another Unicode form (Q109).</summary>
public sealed class ProviderCatalogAliasTests : IDisposable
{
    private readonly ProviderFixture _fixture = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jellymesh-alias-" + Guid.NewGuid().ToString("N"));

    public ProviderCatalogAliasTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "real", "federata", "Serie"));
        Directory.CreateDirectory(Path.Combine(_folder, "real", "serie"));
        Directory.CreateSymbolicLink(Path.Combine(_folder, "link"), Path.Combine(_folder, "real", "federata"));
        Directory.CreateSymbolicLink(Path.Combine(_folder, "alias"), Path.Combine(_folder, "real"));
        _fixture.Store.Configuration.FederatedFolderPath = Path.Combine(_folder, "real", "federata");
    }

    private string Real => Path.Combine(_folder, "real", "serie");

    private string Linked => Path.Combine(_folder, "link", "Serie");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    [Fact]
    public void A_series_reached_through_a_link_to_the_federated_folder_is_federated()
    {
        Assert.True(_fixture.Catalog.IsFederated(new Series { Path = Path.Combine(Linked, "Show") }));
        Assert.True(_fixture.Catalog.IsFederated(new Season { Path = Path.Combine(Linked, "Show", "Season 01") }));
    }

    [Fact]
    public void A_series_whose_name_is_in_another_unicode_form_is_federated()
    {
        _fixture.Store.Configuration.FederatedFolderPath = "/nowhere/Amélie";

        Assert.True(_fixture.Catalog.IsFederated(new Series { Path = "/nowhere/Amélie/Serie/Show" }));
    }

    [Fact]
    public void A_library_that_reaches_the_federated_folder_through_a_link_is_not_offered()
    {
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            Library("Linked", Linked),
            Library("Real", Real)
        ]);

        Assert.Equal(["Real"], _fixture.Catalog.ReadLibraries().Select(library => library.Name));
    }

    [Fact]
    public void A_page_of_a_library_with_a_linked_federated_path_reads_only_under_its_real_path()
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, Real, Linked);
        var real = _fixture.WithFolder(Real);
        var seen = CaptureQuery();

        _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Series, 0, 10);

        Assert.Equal([real.Id], seen()!.AncestorIds);
        Assert.Empty(seen()!.ExcludeItemIds);
    }

    [Fact]
    public void A_page_of_a_library_above_the_federated_folder_through_a_link_leaves_out_the_folder_as_jellyfin_names_it()
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, Path.Combine(_folder, "alias"));
        var federated = _fixture.WithFolder(Path.Combine(_folder, "alias", "federata"));
        var stubSeries = Guid.NewGuid();
        _fixture.Library.Setup(manager => manager.GetItemIds(It.Is<InternalItemsQuery>(query => query.AncestorIds.SequenceEqual(new[] { federated.Id }))))
            .Returns([stubSeries]);
        var seen = CaptureQuery();

        _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Series, 0, 10);

        Assert.Equal([federated.Id, stubSeries], seen()!.ExcludeItemIds);
        Assert.Empty(seen()!.AncestorIds);
    }

    [Fact]
    public void What_the_federated_folder_holds_through_a_link_out_of_it_stays_federated()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "ssd", "serie"));
        var inner = Path.Combine(_folder, "real", "federata", "Out");
        Directory.CreateSymbolicLink(inner, Path.Combine(_folder, "ssd", "serie"));
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns([Library("Inner", inner), Library("Real", Real)]);

        Assert.True(_fixture.Catalog.IsFederated(new Series { Path = Path.Combine(inner, "Show") }));
        Assert.Equal(["Real"], _fixture.Catalog.ReadLibraries().Select(library => library.Name));
    }

    [Fact]
    public void A_page_of_a_library_above_a_federated_folder_that_is_a_link_out_of_it_leaves_out_the_folder()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "lib"));
        Directory.CreateDirectory(Path.Combine(_folder, "outside", "fed"));
        var root = Path.Combine(_folder, "lib", "federata");
        Directory.CreateSymbolicLink(root, Path.Combine(_folder, "outside", "fed"));
        _fixture.Store.Configuration.FederatedFolderPath = root;
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, Path.Combine(_folder, "lib"));
        var federated = _fixture.WithFolder(root);
        _fixture.Library.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([]);
        var seen = CaptureQuery();

        _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Series, 0, 10);

        Assert.Equal([federated.Id], seen()!.ExcludeItemIds);
    }

    [Fact]
    public void A_blank_path_is_not_federated()
    {
        Assert.False(_fixture.Catalog.IsFederated(new Series { Path = "  " }));
    }

    private static VirtualFolderInfo Library(string name, params string[] locations) =>
        new() { Name = name, ItemId = Guid.NewGuid().ToString(), CollectionType = CollectionTypeOptions.tvshows, Locations = locations };

    private Func<InternalItemsQuery?> CaptureQuery()
    {
        InternalItemsQuery? seen = null;
        _fixture.Library.Setup(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(query => seen = query)
            .Returns(new QueryResult<BaseItem>(0, 0, []));
        _fixture.WithDtos();
        return () => seen;
    }
}
