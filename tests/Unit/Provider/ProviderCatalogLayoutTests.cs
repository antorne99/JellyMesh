using System;
using System.IO;
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
    [InlineData("Photo", "/media/Video domestici/Trip/IMG_0001.jpg")]
    [InlineData("Book", "/media/Libri/Title/Title.pdf")]
    [InlineData("MusicVideo", "/media/Video musicali/Clip/Clip.mkv")]
    public void A_look_alike_path_outside_the_federated_folder_is_not_federated(string kind, string path)
    {
        Assert.False(_fixture.Catalog.IsFederated(Item(kind, path)));
    }

    [Theory]
    [InlineData("Series", "/Serie/Show", CollectionTypeOptions.tvshows)]
    [InlineData("Season", "/Serie/Show/Season 01", CollectionTypeOptions.tvshows)]
    [InlineData("Episode", "/Serie/Show/Season 01/Show S01E01 - Server.strm", CollectionTypeOptions.tvshows)]
    [InlineData("Series", "/Serie", CollectionTypeOptions.mixed)]
    [InlineData("Movie", "/Film/Title (2001)", CollectionTypeOptions.movies)]
    [InlineData("Movie", "/Film/Title (2001)/Title (2001) - Server.strm", CollectionTypeOptions.mixed)]
    [InlineData("Folder", "/Film", CollectionTypeOptions.homevideos)]
    [InlineData("Folder", "/Film/Title (2001)", CollectionTypeOptions.homevideos)]
    [InlineData("Video", "/Film/Title (2001)/extras/Clip.mkv", CollectionTypeOptions.homevideos)]
    public void A_title_of_the_0_5_layout_left_by_a_moved_or_cleared_federated_folder_never_reaches_a_peer(string kind, string path, CollectionTypeOptions library)
    {
        foreach (var root in new[] { "/nuova", string.Empty })
        {
            _fixture.Store.Configuration.FederatedFolderPath = root;

            Assert.False(IsVisible(kind, "/vecchia/0f8fad5b-d9cb-469f-a165-70867728950e" + path, library));
            Assert.True(IsVisible(kind, "/media/Vecchia" + path, library));
        }
    }

    [Fact]
    public void A_series_reached_through_a_link_to_an_old_0_5_folder_never_reaches_a_peer()
    {
        var temp = Directory.CreateTempSubdirectory("jm-old-05");
        try
        {
            var series = Directory.CreateDirectory(Path.Combine(temp.FullName, "vecchia", "0f8fad5b-d9cb-469f-a165-70867728950e", "Serie"));
            Directory.CreateDirectory(Path.Combine(series.FullName, "Show"));
            var alias = Path.Combine(temp.FullName, "alias");
            Directory.CreateSymbolicLink(alias, series.FullName);
            _fixture.Store.Configuration.FederatedFolderPath = "/nuova";

            Assert.False(IsVisible("Series", Path.Combine(alias, "Show"), CollectionTypeOptions.tvshows));
        }
        finally
        {
            temp.Delete(true);
        }
    }

    [Theory]
    [InlineData("Series", "/vecchia/Serie/Show", CollectionTypeOptions.tvshows)]
    [InlineData("Movie", "/vecchia/Film/Title (2001)/Title (2001).mkv", CollectionTypeOptions.movies)]
    [InlineData("Series", "/media/Serie/0f8fad5b-d9cb-469f-a165-70867728950e/Show", CollectionTypeOptions.tvshows)]
    [InlineData("Movie", "/media/Film/0f8fad5b-d9cb-469f-a165-70867728950e/Title (2001).mkv", CollectionTypeOptions.movies)]
    [InlineData("Series", "/media/0f8fad5b-d9cb-469f-a165-70867728950e/Show/Serie", CollectionTypeOptions.tvshows)]
    [InlineData("Series", "/media/0f8fad5bd9cb469fa16570867728950e/Serie/Show", CollectionTypeOptions.tvshows)]
    [InlineData("Series", "/media/{0f8fad5b-d9cb-469f-a165-70867728950e}/Serie/Show", CollectionTypeOptions.tvshows)]
    [InlineData("Series", "/media/NAS 0f8fad5b-d9cb-469f-a165-70867728950e/Serie/Show", CollectionTypeOptions.tvshows)]
    [InlineData("Movie", "/media/0f8fad5b-d9cb-469f-a165-70867728950e/Filmati/Title (2001).mkv", CollectionTypeOptions.movies)]
    [InlineData("Movie", "/media/0f8fad5b-d9cb-469f-a165-70867728950e/Film 2001/Title (2001).mkv", CollectionTypeOptions.movies)]
    [InlineData("Movie", "/media/0f8fad5b-d9cb-469f-a165-70867728950e/Title (2001).mkv", CollectionTypeOptions.movies)]
    [InlineData("Movie", "/media/0f8fad5b-d9cb-469f-a165-70867728950e/Film.mkv", CollectionTypeOptions.movies)]
    [InlineData("Audio", "/media/0f8fad5b-d9cb-469f-a165-70867728950e/Musica/Artist/01 Track.flac", CollectionTypeOptions.music)]
    [InlineData("MusicVideo", "/media/0f8fad5b-d9cb-469f-a165-70867728950e/Video musicali/Clip.mkv", CollectionTypeOptions.musicvideos)]
    [InlineData("Video", "/media/0f8fad5b-d9cb-469f-a165-70867728950e/Video domestici/Beach.mkv", CollectionTypeOptions.homevideos)]
    [InlineData("Book", "/media/0f8fad5b-d9cb-469f-a165-70867728950e/Libri/Title/Title.epub", CollectionTypeOptions.books)]
    public void A_look_alike_of_the_0_5_layout_still_reaches_a_peer(string kind, string path, CollectionTypeOptions library)
    {
        foreach (var root in new[] { "/nuova", string.Empty })
        {
            _fixture.Store.Configuration.FederatedFolderPath = root;

            Assert.True(IsVisible(kind, path, library));
        }
    }

    [Fact]
    public void A_real_film_in_the_film_folder_of_a_folder_named_by_a_guid_is_the_accepted_false_positive()
    {
        Assert.False(IsVisible("Movie", "/media/0f8fad5b-d9cb-469f-a165-70867728950e/Film/Title (2001)/Title (2001).mkv", CollectionTypeOptions.movies));
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

    // Whether a peer finds an item of a library of a type that is shared with it.
    private bool IsVisible(string kind, string path, CollectionTypeOptions library)
    {
        var id = Guid.NewGuid();
        _fixture.WithTypedLibrary(ProviderFixture.SharedLibrary, library, "/media");
        _fixture.AddItem(id, Item(kind, path), ProviderFixture.SharedLibrary);
        return _fixture.Catalog.FindVisible(_fixture.PeerCaller, id) is not null;
    }

    private static VirtualFolderInfo Library(string name, CollectionTypeOptions type, params string[] locations) =>
        new() { Name = name, ItemId = Guid.NewGuid().ToString(), CollectionType = type, Locations = locations };

    // An item of the kind Jellyfin makes for a file or folder of that type, with no playback URL.
    private static BaseItem Item(string kind, string path) => kind switch
    {
        "Movie" => new Movie { Path = path },
        "Folder" => new Folder { Path = path },
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
