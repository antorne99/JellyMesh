using System;
using System.Linq;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Provider;

/// <summary>
/// The exclusion of federated items for the 0.6 layout, <c>{root}/{GUID}/{remote library id}/{type}/</c> with its six
/// type folders, next to the 0.4 and 0.5 layouts the migration keeps alongside it. Paths are written from the spec.
/// </summary>
public sealed class ProviderCatalogLayoutTests
{
    private const string Server = "/federata/0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string RemoteLibrary = Server + "/f137a2dd21bbc1b99aa5c0f6bf02a805";

    private readonly ProviderFixture _fixture = new();

    [Theory]
    [InlineData("Movie", RemoteLibrary + "/Film/Title (2001)/Title (2001) - Server.strm")]
    [InlineData("Movie", RemoteLibrary + "/Film/Title (2001)")]
    [InlineData("Series", RemoteLibrary + "/Serie/Show")]
    [InlineData("Season", RemoteLibrary + "/Serie/Show/Season 01")]
    [InlineData("Episode", RemoteLibrary + "/Serie/Show/Season 01/Show S01E01 - Server.strm")]
    [InlineData("Audio", RemoteLibrary + "/Musica/Artist/Album/01 Track.strm")]
    [InlineData("MusicAlbum", RemoteLibrary + "/Musica/Artist/Album")]
    [InlineData("MusicArtist", RemoteLibrary + "/Musica/Artist")]
    [InlineData("MusicVideo", RemoteLibrary + "/Video musicali/Clip/Clip.strm")]
    [InlineData("Video", RemoteLibrary + "/Video domestici/Trip/Beach.strm")]
    [InlineData("Photo", RemoteLibrary + "/Video domestici/Trip/IMG_0001.jpg")]
    [InlineData("PhotoAlbum", RemoteLibrary + "/Video domestici/Trip")]
    [InlineData("Book", RemoteLibrary + "/Libri/Title/Title.epub")]
    [InlineData("Book", RemoteLibrary + "/Libri/Title/Title.pdf")]
    [InlineData("Book", RemoteLibrary + "/Libri/Comic/Comic.cbz")]
    [InlineData("AudioBook", RemoteLibrary + "/Libri/Story/Story.strm")]
    public void An_item_in_a_type_folder_is_federated_without_a_playback_url(string kind, string path)
    {
        Assert.True(_fixture.Catalog.IsFederated(Item(kind, path)));
    }

    [Theory]
    [InlineData(RemoteLibrary)]
    [InlineData(RemoteLibrary + "/Film")]
    [InlineData(RemoteLibrary + "/Serie")]
    [InlineData(RemoteLibrary + "/Musica")]
    [InlineData(RemoteLibrary + "/Video musicali")]
    [InlineData(RemoteLibrary + "/Video domestici")]
    [InlineData(RemoteLibrary + "/Libri")]
    public void The_folder_of_a_remote_library_and_its_type_folders_are_federated(string path)
    {
        Assert.True(_fixture.Catalog.IsFederated(new Folder { Path = path }));
    }

    [Theory]
    [InlineData("Book", RemoteLibrary + "/Libri/Series/Volume 1/Part 2/Title.epub")]
    [InlineData("Photo", RemoteLibrary + "/Video domestici/2024/Summer/Trip/Day 1/IMG_0001.jpg")]
    [InlineData("Audio", RemoteLibrary + "/Musica/Artist/Album/Disc 1/01 Track.strm")]
    [InlineData("Video", RemoteLibrary + "/Video domestici/a/b/c/d/e/clip.strm")]
    public void An_item_nested_deep_in_a_type_folder_is_federated(string kind, string path)
    {
        Assert.True(_fixture.Catalog.IsFederated(Item(kind, path)));
    }

    [Theory]
    [InlineData("/federata/Film/Title (2001)/Title (2001).strm")]
    [InlineData("/federata/Serie/Show/Season 01")]
    [InlineData(Server + "/Film/Title (2001)/Title (2001) - Server.strm")]
    [InlineData(Server + "/Serie/Show/Season 01")]
    [InlineData(RemoteLibrary + "/Film/Title (2001)/Title (2001) - Server.strm")]
    [InlineData(RemoteLibrary + "/Serie/Show/Season 01")]
    public void Every_layout_is_federated_while_the_migration_keeps_them_side_by_side(string path)
    {
        Assert.True(_fixture.Catalog.IsFederated(new Folder { Path = path }));
    }

    [Theory]
    [InlineData("Book", "/media/0f8fad5b-d9cb-469f-a165-70867728950e/Libri/Title/Title.epub")]
    [InlineData("Audio", "/federata-other/f137a2dd21bbc1b99aa5c0f6bf02a805/Musica/Artist/Album/01 Track.strm")]
    [InlineData("Movie", "/media/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Film/Title (2001)/Title (2001).strm")]
    [InlineData("Photo", "/media/Video domestici/Trip/IMG_0001.jpg")]
    [InlineData("Book", "/media/Libri/Title/Title.pdf")]
    [InlineData("MusicVideo", "/media/Video musicali/Clip/Clip.mkv")]
    public void A_look_alike_path_outside_the_federated_folder_is_not_federated(string kind, string path)
    {
        Assert.False(_fixture.Catalog.IsFederated(Item(kind, path)));
    }

    [Fact]
    public void A_placeholder_in_a_shared_library_is_never_found()
    {
        var placeholder = Guid.NewGuid();
        _fixture.AddItem(placeholder, new Book { Path = RemoteLibrary + "/Libri/Title/Title.epub" }, ProviderFixture.SharedLibrary);

        Assert.Null(_fixture.Catalog.FindVisible(_fixture.PeerCaller, placeholder));
    }

    [Fact]
    public void A_page_of_a_library_with_old_and_new_federated_paths_reads_only_under_its_real_path()
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/media/film", Server + "/Film", RemoteLibrary + "/Film");
        var film = _fixture.WithFolder("/media/film");
        InternalItemsQuery? seen = null;
        _fixture.Library.Setup(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(query => seen = query)
            .Returns(new QueryResult<BaseItem>(0, 0, []));
        _fixture.WithDtos();

        _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Equal([film.Id], seen!.AncestorIds);
        Assert.Empty(seen.ExcludeItemIds);
    }

    [Fact]
    public void A_library_with_only_old_and_new_federated_paths_reads_as_empty_and_is_not_offered()
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/federata/Film", Server + "/Film", RemoteLibrary + "/Film");

        var page = _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalRecordCount);
        Assert.Empty(_fixture.Catalog.ReadLibraries());
        _fixture.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public void Libraries_of_new_type_folders_alone_are_not_offered()
    {
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            Library("Mixed", CollectionTypeOptions.movies, "/media/film", RemoteLibrary + "/Film"),
            Library("PerRemoteLibrary", CollectionTypeOptions.tvshows, RemoteLibrary + "/Serie", Server + "/0a1b2c3d4e5f60718293a4b5c6d7e8f9/Serie"),
            Library("LookAlike", CollectionTypeOptions.movies, "/media/0f8fad5b-d9cb-469f-a165-70867728950e/f137a2dd21bbc1b99aa5c0f6bf02a805/Film")
        ]);

        var names = _fixture.Catalog.ReadLibraries().Select(library => library.Name);

        Assert.Equal(["Mixed", "LookAlike"], names);
    }

    private static VirtualFolderInfo Library(string name, CollectionTypeOptions type, params string[] locations) =>
        new() { Name = name, ItemId = Guid.NewGuid().ToString(), CollectionType = type, Locations = locations };

    // An item of the kind Jellyfin makes for a file or folder of that type, with no playback URL.
    private static BaseItem Item(string kind, string path) => kind switch
    {
        "Movie" => new Movie { Path = path },
        "Series" => new Series { Path = path },
        "Season" => new Season { Path = path },
        "Episode" => new Episode { Path = path },
        "Audio" => new Audio { Path = path },
        "MusicAlbum" => new MusicAlbum { Path = path },
        "MusicArtist" => new MusicArtist { Path = path },
        "MusicVideo" => new MusicVideo { Path = path },
        "Video" => new Video { Path = path },
        "Photo" => new Photo { Path = path },
        "PhotoAlbum" => new PhotoAlbum { Path = path },
        "Book" => new Book { Path = path },
        "AudioBook" => new AudioBook { Path = path },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
