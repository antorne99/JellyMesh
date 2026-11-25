using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Provider;

public class ProviderCatalogTests
{
    private readonly ProviderFixture _fixture = new();

    [Theory]
    [InlineData("/federata/Film/x/x.strm", null, true)]
    [InlineData("/federata/Serie/x", null, true)]
    [InlineData("/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Film/x/x - Server.strm", null, true)]
    [InlineData("/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Serie/Show/Season 01", null, true)]
    [InlineData("/federata/0f8fad5b-d9cb-469f-a165-70867728950e", null, true)]
    [InlineData("/federata", null, true)]
    [InlineData("/elsewhere/x.strm", "http://127.0.0.1:8096/JellyMesh/Play/stub", true)]
    [InlineData("/elsewhere/x.strm", "http://127.0.0.1:8096/JellyMesh/Play/stub?sig=0123456789abcdef", true)]
    [InlineData("/media/film/x.mkv", null, false)]
    [InlineData("/federata-other/x.mkv", null, false)]
    [InlineData("/elsewhere/x.strm", "http://cdn.example/film.mp4", false)]
    public void Recognises_what_this_server_federates(string path, string? shortcut, bool expected)
    {
        Assert.Equal(expected, _fixture.Catalog.IsFederated(new Movie { Path = path, ShortcutPath = shortcut }));
    }

    [Fact]
    public void A_series_folder_of_stubs_is_federated_though_it_has_no_url()
    {
        Assert.True(_fixture.Catalog.IsFederated(new Series { Path = "/federata/Serie/Show" }));
    }

    [Fact]
    public void A_stub_stays_federated_after_the_folder_setting_changes()
    {
        _fixture.Store.Configuration.FederatedFolderPath = string.Empty;

        Assert.True(_fixture.Catalog.IsFederated(new Movie { Path = "/federata/Film/x.strm", ShortcutPath = "http://127.0.0.1:8096/JellyMesh/Play/stub" }));
    }

    [Fact]
    public void Finds_only_a_shared_real_item()
    {
        var caller = _fixture.PeerCaller;

        Assert.NotNull(_fixture.Catalog.FindVisible(caller, ProviderFixture.SharedItem));
        Assert.Null(_fixture.Catalog.FindVisible(caller, ProviderFixture.PrivateItem));
        Assert.Null(_fixture.Catalog.FindVisible(caller, ProviderFixture.StubItem));
        Assert.Null(_fixture.Catalog.FindVisible(caller, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("audio", "/federata/0f8fad5b-d9cb-469f-a165-70867728950e/7c9e6679742540de944be07fc1f90ae7/Musica/Artista/Album/01 - Uno.strm")]
    [InlineData("musicvideo", "/federata/Video musicali/Clip/Clip.strm")]
    [InlineData("video", "/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Video domestici/Gita/gita.strm")]
    [InlineData("folder", "/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Video domestici/Gita")]
    [InlineData("book", "/federata/0f8fad5b-d9cb-469f-a165-70867728950e/7c9e6679742540de944be07fc1f90ae7/Libri/Autore/Libro.epub")]
    [InlineData("audiobook", "/federata/Libri/Autore/Libro/01.strm")]
    public void A_stub_of_any_type_never_reaches_a_peer(string type, string path)
    {
        BaseItem stub = type switch
        {
            "audio" => new MediaBrowser.Controller.Entities.Audio.Audio { Path = path },
            "musicvideo" => new MusicVideo { Path = path },
            "video" => new Video { Path = path },
            "folder" => new Folder { Path = path },
            "book" => new Book { Path = path },
            _ => new AudioBook { Path = path }
        };
        var id = Guid.NewGuid();
        _fixture.AddItem(id, stub, ProviderFixture.SharedLibrary);

        Assert.True(_fixture.Catalog.IsFederated(stub));
        Assert.Null(_fixture.Catalog.FindVisible(_fixture.PeerCaller, id));
    }

    [Theory]
    [InlineData("audio", "/vecchia/0f8fad5b-d9cb-469f-a165-70867728950e/7c9e6679742540de944be07fc1f90ae7/Musica/Artista/Album/01 - Uno.strm", "/nuova")]
    [InlineData("audio", "/vecchia/0f8fad5b-d9cb-469f-a165-70867728950e/7c9e6679742540de944be07fc1f90ae7/Musica/Artista/Album/01 - Uno.strm", "")]
    [InlineData("album", "/vecchia/0f8fad5b-d9cb-469f-a165-70867728950e/7c9e6679742540de944be07fc1f90ae7/Musica/Artista/Album", "")]
    [InlineData("folder", "/vecchia/0f8fad5b-d9cb-469f-a165-70867728950e/7c9e6679742540de944be07fc1f90ae7/Video domestici/Gita", "/nuova")]
    [InlineData("book", "/vecchia/0f8fad5b-d9cb-469f-a165-70867728950e/7c9e6679742540de944be07fc1f90ae7/Libri/Autore/Libro.epub", "")]
    [InlineData("audiobook", "/vecchia/0f8fad5b-d9cb-469f-a165-70867728950e/7c9e6679742540de944be07fc1f90ae7/Libri/Autore/Libro/01.strm", "/nuova")]
    public void A_stub_left_by_a_moved_or_cleared_federated_folder_never_reaches_a_peer(string type, string path, string root)
    {
        _fixture.Store.Configuration.FederatedFolderPath = root;
        BaseItem stub = type switch
        {
            "audio" => new MediaBrowser.Controller.Entities.Audio.Audio { Path = path },
            "album" => new MediaBrowser.Controller.Entities.Audio.MusicAlbum { Path = path },
            "folder" => new Folder { Path = path },
            "book" => new Book { Path = path },
            _ => new AudioBook { Path = path }
        };
        var id = Guid.NewGuid();
        _fixture.AddItem(id, stub, ProviderFixture.SharedLibrary);

        Assert.True(_fixture.Catalog.IsFederated(stub));
        Assert.Null(_fixture.Catalog.FindVisible(_fixture.PeerCaller, id));
    }

    [Fact]
    public void A_stub_reached_through_a_link_to_an_old_server_folder_never_reaches_a_peer()
    {
        var temp = Directory.CreateTempSubdirectory("jm-old-root");
        try
        {
            var music = Directory.CreateDirectory(Path.Combine(temp.FullName, "vecchia", "0f8fad5b-d9cb-469f-a165-70867728950e", "7c9e6679742540de944be07fc1f90ae7", "Musica", "Artista"));
            File.WriteAllText(Path.Combine(music.FullName, "01 - Uno.strm"), "stub");
            var alias = Path.Combine(temp.FullName, "alias");
            Directory.CreateSymbolicLink(alias, music.FullName);
            _fixture.Store.Configuration.FederatedFolderPath = "/nuova";

            Assert.True(_fixture.Catalog.IsFederated(new MediaBrowser.Controller.Entities.Audio.Audio { Path = Path.Combine(alias, "01 - Uno.strm") }));
        }
        finally
        {
            temp.Delete(true);
        }
    }

    [Fact]
    public void A_server_folder_name_alone_does_not_make_an_item_federated()
    {
        _fixture.Store.Configuration.FederatedFolderPath = string.Empty;

        Assert.False(_fixture.Catalog.IsFederated(new MediaBrowser.Controller.Entities.Audio.Audio { Path = "/media/0f8fad5b-d9cb-469f-a165-70867728950e/Musica/Artista/01.flac" }));
        Assert.False(_fixture.Catalog.IsFederated(new Book { Path = "/media/Libri/Autore/Libro.epub" }));
    }

    [Theory]
    [InlineData("/vecchia/0f8fad5b-d9cb-469f-a165-70867728950e/7c9e6679742540de944be07fc1f90ae7", false)]
    [InlineData("/vecchia/0f8fad5b-d9cb-469f-a165-70867728950e/7c9e6679742540de944be07fc1f90ae7/", false)]
    [InlineData("/vecchia/0f8fad5b-d9cb-469f-a165-70867728950e", true)]
    public void The_folders_of_a_server_left_by_a_moved_federated_folder_never_reach_a_peer(string path, bool onDisk)
    {
        var temp = Directory.CreateTempSubdirectory("jm-old-root");
        try
        {
            if (onDisk)
            {
                Directory.CreateDirectory(Path.Combine(temp.FullName, "vecchia", "0f8fad5b-d9cb-469f-a165-70867728950e", "7c9e6679742540de944be07fc1f90ae7", "Video domestici"));
                path = temp.FullName + path;
            }

            _fixture.Store.Configuration.FederatedFolderPath = "/nuova";

            Assert.True(_fixture.Catalog.IsFederated(new Folder { Path = path }));
        }
        finally
        {
            temp.Delete(true);
        }
    }

    [Fact]
    public void A_folder_named_as_a_guid_is_federated_only_with_the_folder_of_a_library_in_it()
    {
        var temp = Directory.CreateTempSubdirectory("jm-guid-folder");
        try
        {
            var own = Directory.CreateDirectory(Path.Combine(temp.FullName, "0f8fad5b-d9cb-469f-a165-70867728950e", "Gita"));
            _fixture.Store.Configuration.FederatedFolderPath = string.Empty;

            Assert.False(_fixture.Catalog.IsFederated(new Folder { Path = Path.GetDirectoryName(own.FullName)! }));
            Assert.False(_fixture.Catalog.IsFederated(new Folder { Path = "/media/0f8fad5b-d9cb-469f-a165-70867728950e" }));
            Assert.False(_fixture.Catalog.IsFederated(new Folder { Path = "/media/7c9e6679742540de944be07fc1f90ae7" }));
            Assert.False(_fixture.Catalog.IsFederated(new Folder { Path = "/media/Gita/7c9e6679742540de944be07fc1f90ae7" }));
        }
        finally
        {
            temp.Delete(true);
        }
    }

    [Fact]
    public void A_home_video_library_gives_a_peer_its_photos_and_their_albums()
    {
        _fixture.WithTypedLibrary(ProviderFixture.SharedLibrary, CollectionTypeOptions.homevideos, "/media/casa");
        var photo = Guid.NewGuid();
        var album = Guid.NewGuid();
        var video = Guid.NewGuid();
        _fixture.AddItem(photo, new Photo { Path = "/media/casa/Album/a.jpg" }, ProviderFixture.SharedLibrary);
        _fixture.AddItem(album, new PhotoAlbum { Path = "/media/casa/Album" }, ProviderFixture.SharedLibrary);
        _fixture.AddItem(video, new Video { Path = "/media/casa/gita.mkv" }, ProviderFixture.SharedLibrary);

        Assert.NotNull(_fixture.Catalog.FindVisible(_fixture.PeerCaller, photo));
        Assert.NotNull(_fixture.Catalog.FindVisible(_fixture.PeerCaller, album));
        Assert.NotNull(_fixture.Catalog.FindVisible(_fixture.PeerCaller, video));
    }

    [Theory]
    [InlineData(CollectionTypeOptions.music, true)]
    [InlineData(CollectionTypeOptions.movies, false)]
    [InlineData(CollectionTypeOptions.mixed, false)]
    [InlineData(null, false)]
    public void A_track_and_its_album_are_found_only_in_a_shared_library_that_gives_tracks(CollectionTypeOptions? type, bool found)
    {
        _fixture.WithTypedLibrary(ProviderFixture.SharedLibrary, type, "/media/musica");
        var track = Guid.NewGuid();
        var album = Guid.NewGuid();
        _fixture.AddItem(track, new MediaBrowser.Controller.Entities.Audio.Audio { Path = "/media/musica/a/1.flac" }, ProviderFixture.SharedLibrary);
        _fixture.AddItem(album, new MediaBrowser.Controller.Entities.Audio.MusicAlbum { Path = "/media/musica/a" }, ProviderFixture.SharedLibrary);

        Assert.Equal(found, _fixture.Catalog.FindVisible(_fixture.PeerCaller, track) is not null);
        Assert.Equal(found, _fixture.Catalog.FindVisible(_fixture.PeerCaller, album) is not null);
    }

    [Theory]
    [InlineData(CollectionTypeOptions.movies)]
    [InlineData(CollectionTypeOptions.mixed)]
    [InlineData(null)]
    public void A_film_or_mixed_library_gives_a_peer_no_track_book_or_home_video(CollectionTypeOptions? type)
    {
        _fixture.WithTypedLibrary(ProviderFixture.SharedLibrary, type, "/media/misti");
        BaseItem[] items =
        [
            new MediaBrowser.Controller.Entities.Audio.Audio { Path = "/media/misti/1.flac" },
            new Book { Path = "/media/misti/a.epub" },
            new AudioBook { Path = "/media/misti/b.m4b" },
            new Video { Path = "/media/misti/gita.mkv" },
            new MusicVideo { Path = "/media/misti/clip.mkv" },
            new Folder { Path = "/media/misti/Gita" },
            new Photo { Path = "/media/misti/Album/a.jpg" },
            new PhotoAlbum { Path = "/media/misti/Album" }
        ];
        foreach (var item in items)
        {
            _fixture.AddItem(Guid.NewGuid(), item, ProviderFixture.SharedLibrary);
        }

        Assert.All(items, item => Assert.Null(_fixture.Catalog.FindVisible(_fixture.PeerCaller, item.Id)));
        Assert.NotNull(_fixture.Catalog.FindVisible(_fixture.PeerCaller, ProviderFixture.SharedItem));
    }

    [Theory]
    [InlineData(CollectionTypeOptions.music, RemoteItemType.Movie)]
    [InlineData(CollectionTypeOptions.movies, RemoteItemType.Audio)]
    [InlineData(CollectionTypeOptions.movies, RemoteItemType.Series)]
    [InlineData(CollectionTypeOptions.mixed, RemoteItemType.Book)]
    [InlineData(null, RemoteItemType.Video)]
    [InlineData(CollectionTypeOptions.tvshows, RemoteItemType.Movie)]
    public void A_page_of_a_type_the_library_does_not_give_reads_as_empty(CollectionTypeOptions? libraryType, RemoteItemType type)
    {
        _fixture.WithTypedLibrary(ProviderFixture.SharedLibrary, libraryType, "/media/film");

        var withRoot = _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, type, 0, 10);
        _fixture.Store.Configuration.FederatedFolderPath = string.Empty;
        var withoutRoot = _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, type, 0, 10);

        Assert.Empty(withRoot.Items);
        Assert.Empty(withoutRoot.Items);
        _fixture.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public void A_page_of_a_library_no_longer_on_the_server_reads_as_empty_without_a_federated_folder()
    {
        _fixture.Store.Configuration.FederatedFolderPath = string.Empty;

        var page = _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.PrivateLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Empty(page.Items);
        _fixture.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Theory]
    [InlineData(CollectionTypeOptions.music, true)]
    [InlineData(CollectionTypeOptions.mixed, false)]
    public void A_lookup_of_tracks_gives_only_those_of_a_library_that_gives_tracks(CollectionTypeOptions? type, bool given)
    {
        _fixture.WithTypedLibrary(ProviderFixture.SharedLibrary, type, "/media/musica");
        var track = Guid.NewGuid();
        _fixture.AddItem(track, new MediaBrowser.Controller.Entities.Audio.Audio { Path = "/media/musica/a/1.flac" }, ProviderFixture.SharedLibrary);
        IReadOnlyList<BaseItem>? sent = null;
        _fixture.Dto.Setup(service => service.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<MediaBrowser.Controller.Dto.DtoOptions>(), It.IsAny<User?>(), It.IsAny<BaseItem?>()))
            .Callback<IReadOnlyList<BaseItem>, MediaBrowser.Controller.Dto.DtoOptions, User?, BaseItem?, bool>((items, _, _, _, _) => sent = items)
            .Returns([]);

        _fixture.Catalog.ReadItems(_fixture.PeerCaller, [track], RemoteItemType.Audio);

        Assert.Equal(given ? [track] : [], sent!.Select(item => item.Id));
    }

    [Fact]
    public void An_item_the_user_cannot_see_stays_hidden_even_if_shared()
    {
        _fixture.Library.Setup(manager => manager.GetItemById<BaseItem>(ProviderFixture.SharedItem, It.IsAny<User>())).Returns((BaseItem?)null);

        Assert.Null(_fixture.Catalog.FindVisible(_fixture.PeerCaller, ProviderFixture.SharedItem));
    }

    [Theory]
    [InlineData("/federata/Film")]
    [InlineData("/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Film")]
    [InlineData("/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Serie")]
    [InlineData("/federata")]
    public void A_page_of_a_library_with_federated_paths_reads_only_under_its_real_paths(string federated)
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/media/film", federated, "/media/more/");
        var film = _fixture.WithFolder("/media/film");
        var more = _fixture.WithFolder("/media/more");
        var seen = CaptureQuery();

        var page = _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Equal([film.Id, more.Id], seen()!.AncestorIds);
        Assert.Empty(seen()!.ExcludeItemIds);
        Assert.Equal(ProviderFixture.SharedLibrary, seen()!.ParentId);
        Assert.True(seen()!.Recursive);
        Assert.Equal([BaseItemKind.Movie], seen()!.IncludeItemTypes);
        Assert.Equal(1, page.TotalRecordCount);
    }

    [Fact]
    public void A_page_of_a_library_with_only_real_paths_is_not_narrowed()
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/media/film", "/federata-other/Film");
        var seen = CaptureQuery();

        _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Empty(seen()!.AncestorIds);
        Assert.Empty(seen()!.ExcludeItemIds);
        _fixture.Library.Verify(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Theory]
    [InlineData("/federata/Film", "/federata/Serie")]
    [InlineData("/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Film", "/federata/7c9e6679-7425-40de-944b-e07fc1f90ae7/Film")]
    [InlineData("/federata", "/federata/Film")]
    public void A_library_with_only_federated_paths_reads_as_empty(string first, string second)
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, first, second);

        var page = _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalRecordCount);
        _fixture.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);
        Assert.Empty(_fixture.CatalogLogger.Lines);
    }

    [Theory]
    [InlineData("/federata/Film")]
    [InlineData("/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Film")]
    public void A_library_whose_real_paths_are_not_indexed_yet_reads_as_empty(string federated)
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/media/film", federated);

        var page = _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Equal(0, page.TotalRecordCount);
        _fixture.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);
        AssertDebugLineWithoutPaths();
    }

    [Fact]
    public void A_library_that_is_gone_reads_as_empty()
    {
        _fixture.WithLibrary(ProviderFixture.PrivateLibrary, "/media/film");

        var page = _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Equal(0, page.TotalRecordCount);
        _fixture.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);
        AssertDebugLineWithoutPaths();
    }

    // One Debug line naming the shared library by id and no path.
    private void AssertDebugLineWithoutPaths()
    {
        var line = Assert.Single(_fixture.CatalogLogger.Lines);
        Assert.Equal(LogLevel.Debug, Assert.Single(_fixture.CatalogLogger.Levels));
        Assert.Contains(ProviderFixture.SharedLibrary.ToString(), line, StringComparison.Ordinal);
        Assert.DoesNotContain("/", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_of_a_library_above_the_federated_folder_excludes_everything_under_it_in_the_query()
    {
        _fixture.Store.Configuration.FederatedFolderPath = "/media/federata/";
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/media", "/media/federata/Film");
        var root = _fixture.WithFolder("/media/federata");
        var linked = _fixture.WithFolder("/media/federata/Film");
        var season = Guid.NewGuid();
        _fixture.Library.Setup(manager => manager.GetItemIds(It.Is<InternalItemsQuery>(query => query.AncestorIds.SequenceEqual(new[] { root.Id, linked.Id }) && query.Recursive)))
            .Returns([ProviderFixture.StubItem, season]);
        var seen = CaptureQuery();

        _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Equal([root.Id, linked.Id, ProviderFixture.StubItem, season], seen()!.ExcludeItemIds);
        Assert.Empty(seen()!.AncestorIds);
    }

    [Fact]
    public void A_page_of_a_library_above_the_federated_folder_leaves_out_the_folder_itself()
    {
        _fixture.Store.Configuration.FederatedFolderPath = "/media/federata";
        _fixture.WithTypedLibrary(ProviderFixture.SharedLibrary, CollectionTypeOptions.tvshows, "/media");
        var root = _fixture.WithFolder("/media/federata");
        _fixture.Library.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([]);
        var seen = CaptureQuery();

        _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Series, 0, 10);

        Assert.Contains(root.Id, seen()!.ExcludeItemIds);
    }

    [Fact]
    public void A_page_of_a_library_on_the_filesystem_root_excludes_everything_under_the_federated_folder()
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/", "/federata/Film");
        var root = _fixture.WithFolder("/federata");
        var linked = _fixture.WithFolder("/federata/Film");
        _fixture.Library.Setup(manager => manager.GetItemIds(It.Is<InternalItemsQuery>(query => query.AncestorIds.SequenceEqual(new[] { root.Id, linked.Id }))))
            .Returns([ProviderFixture.StubItem]);
        var seen = CaptureQuery();

        _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Equal([root.Id, linked.Id, ProviderFixture.StubItem], seen()!.ExcludeItemIds);
        Assert.Empty(seen()!.AncestorIds);
    }

    [Fact]
    public void Without_a_federated_folder_a_page_is_not_narrowed()
    {
        _fixture.Store.Configuration.FederatedFolderPath = string.Empty;
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/media/film", "/federata/Film");
        var seen = CaptureQuery();

        _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.Empty(seen()!.AncestorIds);
        Assert.Empty(seen()!.ExcludeItemIds);
    }

    [Fact]
    public void Libraries_with_only_federated_paths_are_not_offered()
    {
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            Library("Mixed", CollectionTypeOptions.movies, "/media/film", "/federata/Film"),
            Library("Flat", CollectionTypeOptions.movies, "/federata/Film"),
            Library("PerServer", CollectionTypeOptions.tvshows, "/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Serie", "/federata/7c9e6679-7425-40de-944b-e07fc1f90ae7/Serie/"),
            Library("Root", CollectionTypeOptions.movies, "/federata"),
            Library("Above", CollectionTypeOptions.movies, "/"),
            Library("Sibling", CollectionTypeOptions.movies, "/federata-other/Film"),
            Library("NoPaths", CollectionTypeOptions.movies)
        ]);

        var names = _fixture.Catalog.ReadLibraries().Select(library => library.Name);

        Assert.Equal(["Mixed", "Above", "Sibling", "NoPaths"], names);
    }

    [Fact]
    public void Libraries_of_every_federated_type_are_offered_as_the_consumer_reads_them()
    {
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            Library("Film", CollectionTypeOptions.movies, "/media/film"),
            Library("Serie", CollectionTypeOptions.tvshows, "/media/serie"),
            Library("Musica", CollectionTypeOptions.music, "/media/musica"),
            Library("Clip", CollectionTypeOptions.musicvideos, "/media/clip"),
            Library("Casa", CollectionTypeOptions.homevideos, "/media/casa"),
            Library("Libri", CollectionTypeOptions.books, "/media/libri"),
            Library("Misti", CollectionTypeOptions.mixed, "/media/misti"),
            new VirtualFolderInfo { Name = "Untyped", ItemId = Guid.NewGuid().ToString(), Locations = ["/media/altro"] },
            Library("Raccolte", CollectionTypeOptions.boxsets, "/config/collections")
        ]);

        var offered = _fixture.Catalog.ReadLibraries().Select(library => (library.Name, library.CollectionType));

        Assert.Equal(
            [("Film", "movies"), ("Serie", "tvshows"), ("Musica", "music"), ("Clip", "musicvideos"), ("Casa", "homevideos"), ("Libri", "books"), ("Misti", "mixed"), ("Untyped", "mixed")],
            offered);
    }

    [Theory]
    [InlineData(CollectionTypeOptions.music)]
    [InlineData(CollectionTypeOptions.books)]
    [InlineData(CollectionTypeOptions.mixed)]
    public void A_library_of_a_new_type_with_only_federated_paths_is_not_offered(CollectionTypeOptions type)
    {
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns([Library("Flat", type, "/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Musica")]);

        Assert.Empty(_fixture.Catalog.ReadLibraries());
    }

    [Fact]
    public void Without_a_federated_folder_every_library_is_offered()
    {
        _fixture.Store.Configuration.FederatedFolderPath = string.Empty;
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns([Library("Flat", CollectionTypeOptions.movies, "/federata/Film")]);

        Assert.Equal(["Flat"], _fixture.Catalog.ReadLibraries().Select(library => library.Name));
    }

    private static VirtualFolderInfo Library(string name, CollectionTypeOptions type, params string[] locations) =>
        new() { Name = name, ItemId = Guid.NewGuid().ToString(), CollectionType = type, Locations = locations };

    private Func<InternalItemsQuery?> CaptureQuery()
    {
        InternalItemsQuery? seen = null;
        _fixture.Library.Setup(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(query => seen = query)
            .Returns(new QueryResult<BaseItem>(0, 1, [new Movie { Path = "/media/film/a.mkv" }]));
        _fixture.WithDtos();
        return () => seen;
    }

    [Fact]
    public void A_stub_past_the_query_is_dropped_so_the_page_comes_up_short_and_nothing_leaks()
    {
        BaseItem stub = new Movie { Id = Guid.NewGuid(), Path = "/federata/Film/x.strm" };
        _fixture.Library.Setup(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>(0, 2, [stub, new Movie { Id = Guid.NewGuid(), Path = "/media/a.mkv" }]));
        IReadOnlyList<BaseItem>? sent = null;
        _fixture.Dto.Setup(service => service.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<MediaBrowser.Controller.Dto.DtoOptions>(), It.IsAny<User?>(), It.IsAny<BaseItem?>()))
            .Callback<IReadOnlyList<BaseItem>, MediaBrowser.Controller.Dto.DtoOptions, User?, BaseItem?, bool>((items, _, _, _, _) => sent = items)
            .Returns([]);

        var page = _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Movie, 0, 10);

        Assert.DoesNotContain(stub, sent!);
        Assert.Equal(2, page.TotalRecordCount);
    }

    [Fact]
    public void A_page_of_series_leaves_out_those_of_an_old_0_5_folder()
    {
        _fixture.Store.Configuration.FederatedFolderPath = "/nuova";
        _fixture.WithTypedLibrary(ProviderFixture.SharedLibrary, CollectionTypeOptions.tvshows, "/media/serie", "/vecchia/0f8fad5b-d9cb-469f-a165-70867728950e/Serie");
        BaseItem left = new Series { Id = Guid.NewGuid(), Path = "/vecchia/0f8fad5b-d9cb-469f-a165-70867728950e/Serie/Show" };
        BaseItem real = new Series { Id = Guid.NewGuid(), Path = "/media/serie/Show" };
        _fixture.Library.Setup(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>(0, 2, [left, real]));
        IReadOnlyList<BaseItem>? sent = null;
        _fixture.Dto.Setup(service => service.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<MediaBrowser.Controller.Dto.DtoOptions>(), It.IsAny<User?>(), It.IsAny<BaseItem?>()))
            .Callback<IReadOnlyList<BaseItem>, MediaBrowser.Controller.Dto.DtoOptions, User?, BaseItem?, bool>((items, _, _, _, _) => sent = items)
            .Returns([]);

        _fixture.Catalog.ReadLibrary(_fixture.PeerCaller, ProviderFixture.SharedLibrary, RemoteItemType.Series, 0, 10);

        Assert.Equal([real], sent!);
    }

    [Fact]
    public void A_lookup_leaves_out_what_the_peer_may_not_reach()
    {
        IReadOnlyList<BaseItem>? sent = null;
        _fixture.Dto.Setup(service => service.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<MediaBrowser.Controller.Dto.DtoOptions>(), It.IsAny<User?>(), It.IsAny<BaseItem?>()))
            .Callback<IReadOnlyList<BaseItem>, MediaBrowser.Controller.Dto.DtoOptions, User?, BaseItem?, bool>((items, _, _, _, _) => sent = items)
            .Returns([]);

        var page = _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem, ProviderFixture.PrivateItem, ProviderFixture.StubItem], RemoteItemType.Movie);

        Assert.Equal(ProviderFixture.SharedItem, Assert.Single(sent!).Id);
        Assert.Equal(1, page.TotalRecordCount);
    }

    [Fact]
    public void A_peer_gets_the_trailers()
    {
        MediaBrowser.Controller.Dto.DtoOptions? options = null;
        _fixture.Dto.Setup(service => service.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<MediaBrowser.Controller.Dto.DtoOptions>(), It.IsAny<User?>(), It.IsAny<BaseItem?>()))
            .Callback<IReadOnlyList<BaseItem>, MediaBrowser.Controller.Dto.DtoOptions, User?, BaseItem?, bool>((_, value, _, _, _) => options = value)
            .Returns([new BaseItemDto { Id = ProviderFixture.SharedItem }]);

        _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie);

        Assert.Contains(MediaBrowser.Model.Querying.ItemFields.RemoteTrailers, options!.Fields);
    }

    [Fact]
    public void A_peer_gets_the_parent_of_each_item_for_the_folders_of_home_videos()
    {
        MediaBrowser.Controller.Dto.DtoOptions? options = null;
        _fixture.Dto.Setup(service => service.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<MediaBrowser.Controller.Dto.DtoOptions>(), It.IsAny<User?>(), It.IsAny<BaseItem?>()))
            .Callback<IReadOnlyList<BaseItem>, MediaBrowser.Controller.Dto.DtoOptions, User?, BaseItem?, bool>((_, value, _, _, _) => options = value)
            .Returns([new BaseItemDto { Id = ProviderFixture.SharedItem }]);

        _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie);

        Assert.Contains(MediaBrowser.Model.Querying.ItemFields.ParentId, options!.Fields);
    }

    [Theory]
    [InlineData("/media/libri/Autore/Libro.EPUB", ".epub")]
    [InlineData("/media/libri/Fumetto.cbz", ".cbz")]
    [InlineData("/media/libri/Senza estensione", null)]
    [InlineData(null, null)]
    public void A_book_sends_only_the_extension_of_its_file(string? path, string? sent)
    {
        var book = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemKind.Book, Path = path };
        _fixture.WithDtos(book);

        _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie);

        Assert.Equal(sent, book.Path);
    }

    [Theory]
    [InlineData("/media/casa/Album/IMG_0001.JPG", ".jpg")]
    [InlineData("/media/casa/Album/Scan.tiff", ".tiff")]
    [InlineData("/media/casa/Album/Senza estensione", null)]
    [InlineData(null, null)]
    public void A_photo_sends_only_the_extension_of_its_file(string? path, string? sent)
    {
        var photo = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemKind.Photo, Path = path };
        _fixture.WithDtos(photo);

        _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie);

        Assert.Equal(sent, photo.Path);
    }

    [Fact]
    public void A_peer_gets_the_size_of_each_photo()
    {
        MediaBrowser.Controller.Dto.DtoOptions? options = null;
        _fixture.Dto.Setup(service => service.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<MediaBrowser.Controller.Dto.DtoOptions>(), It.IsAny<User?>(), It.IsAny<BaseItem?>()))
            .Callback<IReadOnlyList<BaseItem>, MediaBrowser.Controller.Dto.DtoOptions, User?, BaseItem?, bool>((_, value, _, _, _) => options = value)
            .Returns([new BaseItemDto { Id = ProviderFixture.SharedItem }]);

        _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie);

        Assert.Contains(MediaBrowser.Model.Querying.ItemFields.Width, options!.Fields);
        Assert.Contains(MediaBrowser.Model.Querying.ItemFields.Height, options.Fields);
    }

    // Row 560: the rest of the EXIF goes on, as on the consumer (row 470).
    [Theory]
    [InlineData(BaseItemKind.Photo)]
    [InlineData(BaseItemKind.Video)]
    public void No_place_a_photo_was_taken_reaches_a_peer(BaseItemKind kind)
    {
        var dto = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Type = kind,
            Latitude = 45.4642,
            Longitude = 9.19,
            Altitude = 122,
            CameraMake = "Make",
            CameraModel = "Model",
            Width = 4000,
            Height = 3000
        };
        _fixture.WithDtos(dto);

        _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie);

        Assert.Null(dto.Latitude);
        Assert.Null(dto.Longitude);
        Assert.Null(dto.Altitude);
        Assert.Equal("Make", dto.CameraMake);
        Assert.Equal("Model", dto.CameraModel);
        Assert.Equal(4000, dto.Width);
        Assert.Equal(3000, dto.Height);
    }

    [Fact]
    public void The_watch_state_of_this_server_never_reaches_a_peer()
    {
        MediaBrowser.Controller.Dto.DtoOptions? options = null;
        var dto = new BaseItemDto { Id = ProviderFixture.SharedItem, UserData = new UserItemDataDto { Played = true, PlayCount = 3, Key = "k", ItemId = ProviderFixture.SharedItem } };
        _fixture.Dto.Setup(service => service.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<MediaBrowser.Controller.Dto.DtoOptions>(), It.IsAny<User?>(), It.IsAny<BaseItem?>()))
            .Callback<IReadOnlyList<BaseItem>, MediaBrowser.Controller.Dto.DtoOptions, User?, BaseItem?, bool>((_, value, _, _, _) => options = value)
            .Returns([dto]);

        var page = _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie);

        Assert.False(options!.EnableUserData);
        Assert.All(page.Items, item => Assert.Null(item.UserData));
    }

    [Fact]
    public void A_lookup_keeps_to_the_type_asked_for()
    {
        _fixture.WithDtos();

        var page = _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Series);

        Assert.Equal(0, page.TotalRecordCount);
    }

    [Fact]
    public void Quality_comes_from_the_default_video_stream()
    {
        var other = new MediaStream { Type = MediaStreamType.Video, Height = 480, Width = 640, BitRate = 1, Codec = "mpeg2" };
        var main = new MediaStream { Type = MediaStreamType.Video, IsDefault = true, Height = 1080, Width = 1920, BitRate = 5_000_000, Codec = "hevc", ColorTransfer = "smpte2084", ColorPrimaries = "bt2020" };
        _fixture.WithDtos(ProviderFixture.DtoWith(other, main));

        var page = _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie);

        var quality = page.Quality[ProviderFixture.SharedItem.ToString("N")];
        Assert.Equal(1080, quality.Height);
        Assert.Equal(1920, quality.Width);
        Assert.Equal(5_000_000, quality.BitRate);
        Assert.Equal("hevc", quality.Codec);
        Assert.Equal("HDR", quality.VideoRange);
    }

    [Fact]
    public void A_version_without_video_has_no_quality()
    {
        _fixture.WithDtos(ProviderFixture.DtoWith(new MediaStream { Type = MediaStreamType.Audio }));

        Assert.Empty(_fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie).Quality);
    }

    [Fact]
    public void No_file_path_reaches_a_peer()
    {
        var dto = ProviderFixture.DtoWith(new MediaStream { Type = MediaStreamType.Subtitle, Path = "/media/secret.srt" });
        dto.Chapters = [new ChapterInfo { Name = "Intro", ImagePath = "/config/metadata/secret.jpg" }];
        dto.Type = BaseItemKind.Movie;
        dto.Path = "/media/secret.mkv";
        _fixture.WithDtos(dto);

        _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie);

        Assert.Null(dto.Path);
        Assert.Null(dto.MediaSources[0].Path);
        Assert.Null(dto.MediaSources[0].MediaStreams[0].Path);
        Assert.Null(dto.Chapters[0].ImagePath);
        Assert.Equal("Intro", dto.Chapters[0].Name);
    }

    // Row 566: the catalog clears the same fields as PlaybackInfo for a source that is not a file of this server.
    [Theory]
    [InlineData(MediaProtocol.Http, false, false)]
    [InlineData(MediaProtocol.File, true, false)]
    [InlineData(MediaProtocol.File, false, true)]
    public void No_url_of_a_source_that_is_not_a_file_reaches_a_peer(MediaProtocol protocol, bool isRemote, bool isStrm)
    {
        const string Secret = "http://origin.invalid/film.mkv?api_key=strm-secret";
        var strm = Guid.Parse("60000000-0000-0000-0000-00000000000b");
        _fixture.AddItem(strm, new Movie { Path = "/media/film/remote.strm" }, ProviderFixture.SharedLibrary);
        var dto = ProviderFixture.DtoWith(new MediaStream { Type = MediaStreamType.Video, Path = Secret }, new MediaStream { Type = MediaStreamType.Subtitle, IsExternal = true, Path = Secret });
        var source = dto.MediaSources[0];
        source.Id = (isStrm ? strm : ProviderFixture.SharedItem).ToString("N");
        source.Protocol = protocol;
        source.IsRemote = isRemote;
        source.Path = Secret;
        source.EncoderPath = Secret;
        source.RequiredHttpHeaders = new() { ["Authorization"] = Secret };
        _fixture.WithDtos(dto);

        _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie);

        var sent = Assert.Single(dto.MediaSources);
        Assert.Null(sent.Path);
        Assert.Null(sent.EncoderPath);
        Assert.Empty(sent.RequiredHttpHeaders);
        Assert.All(sent.MediaStreams, stream => Assert.Null(stream.Path));
    }

    [Fact]
    public void A_file_of_this_server_keeps_its_headers_in_the_catalog()
    {
        var dto = ProviderFixture.DtoWith(new MediaStream { Type = MediaStreamType.Video, Path = "/media/secret.mkv" });
        dto.MediaSources[0].RequiredHttpHeaders = new() { ["X-Kept"] = "kept" };
        _fixture.WithDtos(dto);

        _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie);

        var sent = Assert.Single(dto.MediaSources);
        Assert.Null(sent.Path);
        Assert.Null(sent.MediaStreams[0].Path);
        Assert.Equal("kept", sent.RequiredHttpHeaders["X-Kept"]);
    }
}

public class ProviderControllerTests
{
    private readonly ProviderFixture _fixture = new();
    private readonly ChangeJournal _journal = new();
    private readonly ProviderController _controller;
    private readonly CapabilitiesController _capabilities;

    public ProviderControllerTests()
    {
        var context = new ControllerContext { HttpContext = new DefaultHttpContext() };
        _controller = new ProviderController(_fixture.Directory, _fixture.Catalog, _journal, _fixture.Slots) { ControllerContext = context };
        _capabilities = new CapabilitiesController(_fixture.Directory) { ControllerContext = context };
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "Film", ItemId = ProviderFixture.SharedLibrary.ToString(), CollectionType = CollectionTypeOptions.movies },
            new VirtualFolderInfo { Name = "Private", ItemId = ProviderFixture.PrivateLibrary.ToString(), CollectionType = CollectionTypeOptions.tvshows },
            new VirtualFolderInfo { Name = "Music", ItemId = Guid.NewGuid().ToString(), CollectionType = CollectionTypeOptions.music }
        ]);
        _fixture.WithDtos();
        _fixture.Library.Setup(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>())).Returns(new QueryResult<BaseItem>(0, 0, []));
    }

    private void NotAPeer() => _fixture.Caller = new User("mario", "auth", "reset");

    [Fact]
    public async Task Capabilities_tell_a_peer_the_protocol_version()
    {
        var result = await _capabilities.GetCapabilities();

        Assert.Equal(ProviderProtocol.Version, result.Value!.ProtocolVersion);
    }

    [Fact]
    public async Task Capabilities_advertise_the_version_that_serves_collections()
    {
        var result = await _capabilities.GetCapabilities();

        Assert.True(result.Value!.ProtocolVersion >= ProviderProtocol.CollectionsVersion);
    }

    [Fact]
    public async Task Capabilities_answer_anyone_else_like_a_stock_server()
    {
        NotAPeer();

        var result = await _capabilities.GetCapabilities();

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Every_route_is_closed_to_anyone_else()
    {
        NotAPeer();

        Assert.Equal(403, ((StatusCodeResult)(await _controller.GetLibraries()).Result!).StatusCode);
        Assert.Equal(403, ((StatusCodeResult)(await _controller.GetItems(RemoteItemType.Movie, ProviderFixture.SharedLibrary, null)).Result!).StatusCode);
        Assert.Equal(403, ((StatusCodeResult)(await _controller.GetChanges(null)).Result!).StatusCode);
        Assert.Equal(403, ((StatusCodeResult)await _controller.ReleaseStream("play")).StatusCode);
        Assert.Equal(403, ((StatusCodeResult)await _controller.RenewStream("play")).StatusCode);
    }

    // The store filter turns this into a 503: without the peer list no caller is known, and none is refused as a stranger.
    [Fact]
    public async Task While_the_peers_cannot_be_read_no_route_answers()
    {
        _fixture.Store.Unavailable = true;

        await Assert.ThrowsAsync<StoreSuspendedException>(() => _controller.GetLibraries());
        await Assert.ThrowsAsync<StoreSuspendedException>(() => _controller.GetChanges(null));
        await Assert.ThrowsAsync<StoreSuspendedException>(() => _capabilities.GetCapabilities());
    }

    [Fact]
    public async Task Libraries_are_only_those_shared()
    {
        var result = await _controller.GetLibraries();

        var library = Assert.Single(result.Value!.Items);
        Assert.Equal(ProviderFixture.SharedLibrary.ToString("N"), library.Id);
        Assert.Equal("movies", library.CollectionType);
    }

    [Fact]
    public async Task A_shared_library_left_with_only_federated_paths_is_neither_listed_nor_read()
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/federata/0f8fad5b-d9cb-469f-a165-70867728950e/Film");

        Assert.Empty((await _controller.GetLibraries()).Value!.Items);
        Assert.Equal(0, (await _controller.GetItems(RemoteItemType.Movie, ProviderFixture.SharedLibrary, null)).Value!.TotalRecordCount);
        _fixture.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task A_library_that_is_not_shared_is_not_there()
    {
        var result = await _controller.GetItems(RemoteItemType.Movie, ProviderFixture.PrivateLibrary, null);

        Assert.IsType<NotFoundResult>(result.Result);
        _fixture.Library.Verify(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task Items_need_a_library_or_identifiers()
    {
        Assert.IsType<NotFoundResult>((await _controller.GetItems(RemoteItemType.Movie, null, null)).Result);
    }

    [Fact]
    public async Task A_page_is_capped_however_much_is_asked()
    {
        InternalItemsQuery? seen = null;
        _fixture.Library.Setup(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(query => seen = query)
            .Returns(new QueryResult<BaseItem>(0, 0, []));

        await _controller.GetItems(RemoteItemType.Movie, ProviderFixture.SharedLibrary, null, 0, int.MaxValue);

        Assert.Equal(ProviderProtocol.MaxItemsPerPage, seen!.Limit);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    public async Task A_bad_page_is_refused(int start, int limit)
    {
        Assert.IsType<BadRequestResult>((await _controller.GetItems(RemoteItemType.Movie, ProviderFixture.SharedLibrary, null, start, limit)).Result);
    }

    [Fact]
    public async Task A_lookup_is_capped()
    {
        var ids = string.Join(',', Enumerable.Range(0, ProviderProtocol.MaxItemsPerPage + 1).Select(_ => Guid.NewGuid().ToString("N")));

        Assert.IsType<BadRequestResult>((await _controller.GetItems(RemoteItemType.Movie, null, ids)).Result);
    }

    [Theory]
    [InlineData(RemoteItemType.Audio, BaseItemKind.Audio, CollectionTypeOptions.music)]
    [InlineData(RemoteItemType.MusicVideo, BaseItemKind.MusicVideo, CollectionTypeOptions.musicvideos)]
    [InlineData(RemoteItemType.Video, BaseItemKind.Video, CollectionTypeOptions.homevideos)]
    [InlineData(RemoteItemType.Folder, BaseItemKind.Folder, CollectionTypeOptions.homevideos)]
    [InlineData(RemoteItemType.Photo, BaseItemKind.Photo, CollectionTypeOptions.homevideos)]
    [InlineData(RemoteItemType.PhotoAlbum, BaseItemKind.PhotoAlbum, CollectionTypeOptions.homevideos)]
    [InlineData(RemoteItemType.Book, BaseItemKind.Book, CollectionTypeOptions.books)]
    [InlineData(RemoteItemType.AudioBook, BaseItemKind.AudioBook, CollectionTypeOptions.books)]
    public async Task A_page_of_a_new_type_reads_that_type(RemoteItemType type, BaseItemKind kind, CollectionTypeOptions library)
    {
        _fixture.WithTypedLibrary(ProviderFixture.SharedLibrary, library, "/media/film");
        InternalItemsQuery? seen = null;
        _fixture.Library.Setup(manager => manager.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(query => seen = query)
            .Returns(new QueryResult<BaseItem>(0, 0, []));

        Assert.NotNull((await _controller.GetItems(type, ProviderFixture.SharedLibrary, null)).Value);
        Assert.Equal([kind], seen!.IncludeItemTypes);
    }

    [Theory]
    [InlineData((RemoteItemType)99)]
    public async Task A_type_the_provider_does_not_share_is_refused(RemoteItemType type)
    {
        Assert.IsType<BadRequestResult>((await _controller.GetItems(type, ProviderFixture.SharedLibrary, null)).Result);
        Assert.IsType<BadRequestResult>((await _controller.GetItems(type, null, Guid.NewGuid().ToString("N"))).Result);
    }

    [Fact]
    public async Task A_lookup_with_a_bad_identifier_is_refused()
    {
        Assert.IsType<BadRequestResult>((await _controller.GetItems(RemoteItemType.Movie, null, "not-a-guid")).Result);
    }

    [Fact]
    public async Task The_feed_starts_with_a_position()
    {
        var result = await _controller.GetChanges(null);

        Assert.True(result.Value!.Reset);
        Assert.NotEmpty(result.Value.Cursor);
    }

    [Fact]
    public async Task The_feed_shows_only_what_the_peer_may_see()
    {
        var cursor = (await _controller.GetChanges(null)).Value!.Cursor;
        _journal.Record(ProviderFixture.SharedItem);
        _journal.Record(ProviderFixture.PrivateItem);
        _journal.Record(ProviderFixture.StubItem);
        _journal.Record(Guid.NewGuid());

        var result = await _controller.GetChanges(cursor);

        var change = Assert.Single(result.Value!.Changes);
        Assert.Equal(ProviderFixture.SharedItem.ToString("N"), change.Id);
        Assert.Equal("Movie", change.Type);
        Assert.Equal([ProviderFixture.SharedLibrary.ToString("N")], change.LibraryIds);
    }

    [Theory]
    [InlineData(CollectionTypeOptions.movies, false)]
    [InlineData(CollectionTypeOptions.mixed, false)]
    [InlineData(CollectionTypeOptions.homevideos, true)]
    public async Task The_feed_announces_an_item_only_from_a_library_that_gives_its_type(CollectionTypeOptions type, bool announced)
    {
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns(
            [new VirtualFolderInfo { Name = "Shared", ItemId = ProviderFixture.SharedLibrary.ToString(), CollectionType = type, Locations = ["/media/film"] }]);
        var folder = Guid.NewGuid();
        _fixture.AddItem(folder, new Folder { Path = "/media/film/Extra" }, ProviderFixture.SharedLibrary);
        var cursor = (await _controller.GetChanges(null)).Value!.Cursor;
        _journal.Record(folder);

        var changes = (await _controller.GetChanges(cursor)).Value!.Changes;

        Assert.Equal(announced ? [(folder.ToString("N"), "Folder")] : [], changes.Select(change => (change.Id, change.Type)));
        Assert.All(changes, change => Assert.Equal([ProviderFixture.SharedLibrary.ToString("N")], change.LibraryIds));
    }

    // The album of a track and some of its tracks, as the library answers for the peer.
    private (Guid Album, Guid[] Tracks, Func<InternalItemsQuery?> Query) WithAlbum(CollectionTypeOptions? type, params string[] trackPaths)
    {
        _fixture.WithTypedLibrary(ProviderFixture.SharedLibrary, type, "/media/musica");
        var album = Guid.NewGuid();
        _fixture.AddItem(album, new MediaBrowser.Controller.Entities.Audio.MusicAlbum { Path = "/media/musica/A" }, ProviderFixture.SharedLibrary);
        var tracks = trackPaths.Select(path => new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid(), Path = path }).ToList();
        foreach (var track in tracks)
        {
            _fixture.AddItem(track.Id, track, ProviderFixture.SharedLibrary);
        }

        InternalItemsQuery? seen = null;
        _fixture.Library.Setup(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.ParentId == album)))
            .Callback<InternalItemsQuery>(query => seen = query)
            .Returns(tracks.Cast<BaseItem>().ToList());
        return (album, tracks.Select(track => track.Id).ToArray(), () => seen);
    }

    [Fact]
    public async Task An_album_change_reaches_the_peer_as_its_tracks()
    {
        var (album, tracks, query) = WithAlbum(CollectionTypeOptions.music, "/media/musica/A/1.flac", "/media/musica/A/2.flac");
        var cursor = (await _controller.GetChanges(null)).Value!.Cursor;
        _journal.Record(album);

        var changes = (await _controller.GetChanges(cursor)).Value!.Changes;

        Assert.Equal(tracks.Select(id => (id.ToString("N"), "Audio")), changes.Select(change => (change.Id, change.Type)));
        Assert.All(changes, change => Assert.Equal([ProviderFixture.SharedLibrary.ToString("N")], change.LibraryIds));
        Assert.Same(_fixture.Peer, query()!.User);
        Assert.True(query()!.Recursive);
        Assert.Equal([BaseItemKind.Audio], query()!.IncludeItemTypes);
    }

    [Fact]
    public async Task An_album_change_leaves_out_the_stubs_among_its_tracks()
    {
        var (album, tracks, _) = WithAlbum(
            CollectionTypeOptions.music,
            "/media/musica/A/1.flac",
            "/federata/0f8fad5b-d9cb-469f-a165-70867728950e/7c9e6679742540de944be07fc1f90ae7/Musica/A/2.strm");
        var cursor = (await _controller.GetChanges(null)).Value!.Cursor;
        _journal.Record(album);

        var changes = (await _controller.GetChanges(cursor)).Value!.Changes;

        Assert.Equal([tracks[0].ToString("N")], changes.Select(change => change.Id));
    }

    [Fact]
    public async Task A_track_changed_with_its_album_is_announced_once()
    {
        var (album, tracks, _) = WithAlbum(CollectionTypeOptions.music, "/media/musica/A/1.flac", "/media/musica/A/2.flac");
        var cursor = (await _controller.GetChanges(null)).Value!.Cursor;
        _journal.Record(tracks[0]);
        _journal.Record(album);

        var changes = (await _controller.GetChanges(cursor)).Value!.Changes;

        Assert.Equal(tracks.Select(id => id.ToString("N")), changes.Select(change => change.Id));
    }

    [Theory]
    [InlineData(CollectionTypeOptions.movies)]
    [InlineData(CollectionTypeOptions.mixed)]
    [InlineData(null)]
    public async Task An_album_in_a_library_that_gives_no_tracks_announces_nothing(CollectionTypeOptions? type)
    {
        var (album, _, _) = WithAlbum(type, "/media/musica/A/1.flac");
        var cursor = (await _controller.GetChanges(null)).Value!.Cursor;
        _journal.Record(album);

        Assert.Empty((await _controller.GetChanges(cursor)).Value!.Changes);
    }

    [Fact]
    public async Task A_change_of_the_share_resets_the_feed()
    {
        var cursor = (await _controller.GetChanges(null)).Value!.Cursor;
        _fixture.Store.Configuration.Peers[0].SharedLibraryIds.Add(ProviderFixture.PrivateLibrary.ToString("N"));

        Assert.True((await _controller.GetChanges(cursor)).Value!.Reset);
    }

    [Fact]
    public async Task A_bad_feed_limit_is_refused()
    {
        Assert.IsType<BadRequestResult>((await _controller.GetChanges(null, 0)).Result);
    }

    [Fact]
    public async Task Renewal_keeps_the_slot_of_the_caller()
    {
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "play")!.Dispose();
        _fixture.Clock.Advance(StreamSlots.IdleTimeout - TimeSpan.FromSeconds(1));

        var result = await _controller.RenewStream("play");
        _fixture.Clock.Advance(TimeSpan.FromSeconds(2));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task Renewing_a_slot_that_is_gone_takes_none()
    {
        Assert.IsType<NoContentResult>(await _controller.RenewStream("gone"));

        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task Release_frees_the_slot_of_the_caller_only()
    {
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "play")!.Dispose();
        var other = Guid.NewGuid();
        _fixture.Slots.TryEnter(other, 1, "play")!.Dispose();

        await _controller.ReleaseStream("play");

        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
        Assert.Equal(1, _fixture.Slots.Count(other));
    }
}

public class PeerAdminControllerTests
{
    private readonly ProviderFixture _fixture = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly User _admin = new("admin", "auth", "reset");
    private readonly PeerAdminController _controller;

    public PeerAdminControllerTests()
    {
        _admin.Permissions.Add(new Permission(PermissionKind.IsAdministrator, true));
        _users.Setup(users => users.GetUserById(_fixture.Peer.Id)).Returns(_fixture.Peer);
        _users.Setup(users => users.GetUserById(_admin.Id)).Returns(_admin);
        _users.Setup(users => users.GetUsers()).Returns([_fixture.Peer, _admin]);
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "Film", ItemId = ProviderFixture.SharedLibrary.ToString(), CollectionType = CollectionTypeOptions.movies }
        ]);
        _fixture.Store.Configuration.Peers.Clear();
        _controller = new PeerAdminController(_fixture.Store, _users.Object, _fixture.Catalog, _fixture.Slots);
    }

    private static SetPeerRequest Request(int limit = 2) => new([ProviderFixture.SharedLibrary.ToString("N")], limit);

    [Fact]
    public void Connects_a_non_admin_user()
    {
        Assert.IsType<NoContentResult>(_controller.SetPeer(_fixture.Peer.Id, Request()));

        var peer = Assert.Single(_fixture.Store.Configuration.Peers);
        Assert.Equal(2, peer.MaxStreams);
        Assert.Equal([ProviderFixture.SharedLibrary.ToString("N")], peer.SharedLibraryIds);
    }

    [Fact]
    public void Saving_again_replaces_what_is_shared()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request(2));
        _controller.SetPeer(_fixture.Peer.Id, new SetPeerRequest([], 5));

        var peer = Assert.Single(_fixture.Store.Configuration.Peers);
        Assert.Equal(5, peer.MaxStreams);
        Assert.Empty(peer.SharedLibraryIds);
    }

    [Fact]
    public void An_administrator_cannot_be_a_peer()
    {
        Assert.IsType<BadRequestResult>(_controller.SetPeer(_admin.Id, Request()));
        Assert.Empty(_fixture.Store.Configuration.Peers);
    }

    [Fact]
    public void An_unknown_user_is_refused()
    {
        Assert.IsType<NotFoundResult>(_controller.SetPeer(Guid.NewGuid(), Request()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_limit_must_be_positive(int limit)
    {
        Assert.IsType<BadRequestResult>(_controller.SetPeer(_fixture.Peer.Id, Request(limit)));
    }

    [Fact]
    public void A_library_with_only_federated_paths_is_dropped_from_a_save_and_reported()
    {
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/federata/Film");

        Assert.Empty(_controller.GetOptions().Value!.Libraries);
        var result = Assert.IsType<OkObjectResult>(_controller.SetPeer(_fixture.Peer.Id, Request()));

        Assert.Equal([ProviderFixture.SharedLibrary.ToString("N")], Assert.IsType<SetPeerResponse>(result.Value).DroppedLibraryIds);
        Assert.Empty(Assert.Single(_fixture.Store.Configuration.Peers).SharedLibraryIds);
    }

    [Fact]
    public void An_unknown_library_is_dropped_and_what_is_offered_is_saved()
    {
        TwoLibraries();
        var unknown = Guid.NewGuid().ToString("N");

        var result = Assert.IsType<OkObjectResult>(_controller.SetPeer(_fixture.Peer.Id, new SetPeerRequest([unknown, ProviderFixture.PrivateLibrary.ToString("N")], 1)));

        Assert.Equal([unknown], Assert.IsType<SetPeerResponse>(result.Value).DroppedLibraryIds);
        Assert.Equal([ProviderFixture.PrivateLibrary.ToString("N")], Assert.Single(_fixture.Store.Configuration.Peers).SharedLibraryIds);
    }

    [Fact]
    public void A_saved_library_no_longer_offered_is_dropped_on_the_next_save_and_reported()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, Both());
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "Altra", ItemId = ProviderFixture.PrivateLibrary.ToString(), CollectionType = CollectionTypeOptions.movies }
        ]);

        var result = Assert.IsType<OkObjectResult>(_controller.SetPeer(_fixture.Peer.Id, OnlyPrivate()));

        Assert.Equal([ProviderFixture.SharedLibrary.ToString("N")], Assert.IsType<SetPeerResponse>(result.Value).DroppedLibraryIds);
        Assert.Equal([ProviderFixture.PrivateLibrary.ToString("N")], Assert.Single(_fixture.Store.Configuration.Peers).SharedLibraryIds);
    }

    [Fact]
    public void A_save_with_nothing_to_drop_reports_nothing()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, Both());

        Assert.IsType<NoContentResult>(_controller.SetPeer(_fixture.Peer.Id, OnlyPrivate()));
    }

    [Fact]
    public async Task Dropping_a_library_no_longer_offered_frees_the_slots_on_it_even_when_it_is_sent_again()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request());
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "play", ProviderFixture.SharedItem)!.Dispose();
        _fixture.WithLibrary(ProviderFixture.SharedLibrary, "/federata/Film");

        _controller.SetPeer(_fixture.Peer.Id, Request());

        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
        await _fixture.Closer.CloseAbandonedAsync();
        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public void Options_offer_only_non_admin_users()
    {
        var options = _controller.GetOptions().Value!;

        Assert.Equal(_fixture.Peer.Id, Assert.Single(options.Users).Id);
    }

    [Fact]
    public void The_list_shows_the_streams_in_use()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request());
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "play")!.Dispose();

        var peers = (IReadOnlyList<PeerStatus>)_controller.GetPeers().Value!;

        Assert.Equal(1, Assert.Single(peers).ActiveStreams);
    }

    [Fact]
    public void Removing_a_peer_leaves_the_user_an_ordinary_one()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request());

        Assert.IsType<NoContentResult>(_controller.RemovePeer(_fixture.Peer.Id));

        Assert.Empty(_fixture.Store.Configuration.Peers);
        Assert.Null(_fixture.Directory.Find(_fixture.Peer.Id));
    }

    [Fact]
    public void Removing_a_peer_frees_its_slots_at_once()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request());
        using var running = _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "play");

        _controller.RemovePeer(_fixture.Peer.Id);

        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public void A_higher_limit_frees_nothing()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request());
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "play", ProviderFixture.SharedItem)!.Dispose();

        _controller.SetPeer(_fixture.Peer.Id, Request(3));

        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task A_lower_limit_ends_what_no_longer_fits_and_keeps_the_rest_counted()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request(3));
        var cut = new List<string>();
        using var oldest = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "oldest", ProviderFixture.SharedItem, () => cut.Add("oldest"));
        using var middle = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "middle", ProviderFixture.SharedItem, () => cut.Add("middle"));
        using var newest = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "newest", ProviderFixture.SharedItem, () => cut.Add("newest"));

        _controller.SetPeer(_fixture.Peer.Id, Request(1));

        Assert.Equal(["middle", "newest"], cut.Order(StringComparer.Ordinal));
        Assert.Equal(3, _fixture.Slots.Count(_fixture.Peer.Id));
        await _fixture.Closer.CloseAbandonedAsync();
        VerifyClosed("middle");
        VerifyClosed("newest");
        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public void A_limit_lowered_to_what_is_running_ends_nothing()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request(3));
        var cut = false;
        using var one = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "one", ProviderFixture.SharedItem, () => cut = true);
        using var two = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "two", ProviderFixture.SharedItem, () => cut = true);

        _controller.SetPeer(_fixture.Peer.Id, Request(2));

        Assert.False(cut);
        Assert.Equal(2, _fixture.Slots.Count(_fixture.Peer.Id));
        Assert.Empty(_fixture.Slots.TakeClosing());
    }

    [Fact]
    public async Task A_save_that_lowers_the_limit_and_takes_a_library_away_counts_what_was_lost_first()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, new SetPeerRequest(Both().SharedLibraryIds, 3));
        var cut = new List<string>();
        using var lost = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "lost", ProviderFixture.SharedItem, () => cut.Add("lost"));
        using var older = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "older", ProviderFixture.PrivateItem, () => cut.Add("older"));
        using var newer = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "newer", ProviderFixture.PrivateItem, () => cut.Add("newer"));

        _controller.SetPeer(_fixture.Peer.Id, new SetPeerRequest(OnlyPrivate().SharedLibraryIds, 1));

        Assert.Equal(["lost", "newer"], cut.Order(StringComparer.Ordinal));
        Assert.Equal(3, _fixture.Slots.Count(_fixture.Peer.Id));
        await _fixture.Closer.CloseAbandonedAsync();
        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task A_save_that_takes_a_library_away_can_leave_nothing_more_to_cut_for_the_lower_limit()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, new SetPeerRequest(Both().SharedLibraryIds, 3));
        var cut = new List<string>();
        using var lost = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "lost", ProviderFixture.SharedItem, () => cut.Add("lost"));
        using var kept = _fixture.Slots.TryEnter(_fixture.Peer.Id, 3, "kept", ProviderFixture.PrivateItem, () => cut.Add("kept"));

        _controller.SetPeer(_fixture.Peer.Id, new SetPeerRequest(OnlyPrivate().SharedLibraryIds, 1));

        Assert.Equal(["lost"], cut);
        Assert.Equal(2, _fixture.Slots.Count(_fixture.Peer.Id));
        await _fixture.Closer.CloseAbandonedAsync();
        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task Taking_a_library_away_frees_the_slots_on_it_once_their_transcodes_are_closed()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request());
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "play", ProviderFixture.SharedItem)!.Dispose();

        _controller.SetPeer(_fixture.Peer.Id, new SetPeerRequest([], 2));

        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
        await _fixture.Closer.CloseAbandonedAsync();
        VerifyClosed("play");
        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task Taking_a_library_away_leaves_the_slot_of_what_is_still_shared_counted()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, Both());
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "lost", ProviderFixture.SharedItem)!.Dispose();
        using var kept = _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "kept", ProviderFixture.PrivateItem);

        _controller.SetPeer(_fixture.Peer.Id, OnlyPrivate());
        await _fixture.Closer.CloseAbandonedAsync();

        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
        Assert.NotNull(_fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "another", ProviderFixture.PrivateItem));
        Assert.Null(_fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "over", ProviderFixture.PrivateItem));
    }

    [Fact]
    public void An_added_library_frees_nothing()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, Request());
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "play", ProviderFixture.SharedItem)!.Dispose();

        _controller.SetPeer(_fixture.Peer.Id, Both());

        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public void Removing_a_peer_frees_all_its_slots()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, Both());
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "one", ProviderFixture.SharedItem)!.Dispose();
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "two", ProviderFixture.PrivateItem)!.Dispose();

        _controller.RemovePeer(_fixture.Peer.Id);

        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public void Saving_the_same_grant_again_leaves_the_slots()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request());
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "play")!.Dispose();

        _controller.SetPeer(_fixture.Peer.Id, Request());

        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    private void VerifyClosed(string playSession)
    {
        _fixture.Transcodes.Verify(manager => manager.KillTranscodingJobs(It.IsAny<string>(), playSession, It.IsAny<Func<string, bool>>()), Times.Once);
    }

    private void TwoLibraries()
    {
        _fixture.Library.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "Film", ItemId = ProviderFixture.SharedLibrary.ToString(), CollectionType = CollectionTypeOptions.movies },
            new VirtualFolderInfo { Name = "Altra", ItemId = ProviderFixture.PrivateLibrary.ToString(), CollectionType = CollectionTypeOptions.movies }
        ]);
    }

    private static SetPeerRequest Both() => new([ProviderFixture.SharedLibrary.ToString("N"), ProviderFixture.PrivateLibrary.ToString("N")], 2);

    private static SetPeerRequest OnlyPrivate() => new([ProviderFixture.PrivateLibrary.ToString("N")], 2);

    [Fact]
    public void Removing_a_peer_cuts_all_it_is_playing()
    {
        _controller.SetPeer(_fixture.Peer.Id, Request());
        var cut = 0;
        using var first = _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "one", ProviderFixture.SharedItem, () => cut++);
        using var second = _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "two", ProviderFixture.PrivateItem, () => cut++);

        _controller.RemovePeer(_fixture.Peer.Id);

        Assert.Equal(2, cut);
    }

    [Fact]
    public void Taking_a_library_away_cuts_only_what_plays_from_it()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, Both());
        var cut = new List<string>();
        using var lost = _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "lost", ProviderFixture.SharedItem, () => cut.Add("lost"));
        using var kept = _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "kept", ProviderFixture.PrivateItem, () => cut.Add("kept"));

        _controller.SetPeer(_fixture.Peer.Id, OnlyPrivate());

        Assert.Equal(["lost"], cut);
    }

    [Fact]
    public void An_item_also_in_a_library_still_shared_is_not_cut()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, Both());
        var item = new MediaBrowser.Controller.Entities.Movies.Movie { Path = "/media/film/both.mkv", Id = Guid.NewGuid() };
        _fixture.Library.Setup(manager => manager.GetItemById<MediaBrowser.Controller.Entities.BaseItem>(item.Id, It.IsAny<User>())).Returns(item);
        _fixture.Library.Setup(manager => manager.GetCollectionFolders(item)).Returns(
        [
            new MediaBrowser.Controller.Entities.Folder { Id = ProviderFixture.SharedLibrary },
            new MediaBrowser.Controller.Entities.Folder { Id = ProviderFixture.PrivateLibrary }
        ]);
        var cut = false;
        using var running = _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "both", item.Id, () => cut = true);

        _controller.SetPeer(_fixture.Peer.Id, OnlyPrivate());

        Assert.False(cut);
        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public void A_higher_limit_an_added_library_or_the_same_grant_cuts_nothing()
    {
        TwoLibraries();
        _controller.SetPeer(_fixture.Peer.Id, Request());
        var cut = false;
        using var running = _fixture.Slots.TryEnter(_fixture.Peer.Id, 2, "play", ProviderFixture.SharedItem, () => cut = true);

        _controller.SetPeer(_fixture.Peer.Id, Request(3));
        _controller.SetPeer(_fixture.Peer.Id, Request(3));
        _controller.SetPeer(_fixture.Peer.Id, Both());

        Assert.False(cut);
    }

    [Fact]
    public void Removing_an_unknown_peer_says_so()
    {
        Assert.IsType<NotFoundResult>(_controller.RemovePeer(Guid.NewGuid()));
    }
}

public class ProviderConfigurationTests
{
    [Fact]
    public void A_transcode_closes_at_the_expiry_of_its_slot_by_default()
    {
        Assert.True(new PluginConfiguration().CloseTranscodeAtSlotExpiry);
    }
}

public class ChangeFeedRecorderTests
{
    private readonly ProviderFixture _fixture = new();
    private readonly ChangeJournal _journal = new();

    private async Task<string> StartAsync()
    {
        await new ChangeFeedRecorder(_fixture.Library.Object, _fixture.Catalog, _journal).StartAsync(default);
        return _journal.Read(null, "share", 10).Cursor;
    }

#pragma warning disable CA1849 // These are plain events: there is nothing to await.
    private void RaiseAdded(BaseItem item)
    {
        _fixture.Library.Raise(manager => manager.ItemAdded += null, _fixture.Library.Object, new ItemChangeEventArgs { Item = item });
    }

    private void RaiseUpdated(BaseItem item)
    {
        _fixture.Library.Raise(manager => manager.ItemUpdated += null, _fixture.Library.Object, new ItemChangeEventArgs { Item = item });
    }
#pragma warning restore CA1849

    [Fact]
    public async Task Records_an_added_film()
    {
        var cursor = await StartAsync();
        var movie = new Movie { Id = Guid.NewGuid(), Path = "/media/a.mkv" };

        RaiseAdded(movie);

        Assert.Equal([movie.Id], _journal.Read(cursor, "share", 10).ItemIds);
    }

    [Fact]
    public async Task Records_an_updated_episode()
    {
        var cursor = await StartAsync();
        var episode = new Episode { Id = Guid.NewGuid(), Path = "/media/a.mkv" };

        RaiseUpdated(episode);

        Assert.Equal([episode.Id], _journal.Read(cursor, "share", 10).ItemIds);
    }

    [Fact]
    public async Task A_stub_never_enters_the_feed()
    {
        var cursor = await StartAsync();

        RaiseAdded(new Movie { Id = Guid.NewGuid(), Path = "/federata/Film/x.strm" });

        Assert.Empty(_journal.Read(cursor, "share", 10).ItemIds);
    }

    [Fact]
    public async Task What_is_not_in_a_catalog_stays_out()
    {
        var cursor = await StartAsync();

        RaiseAdded(new CollectionFolder { Id = Guid.NewGuid(), Path = "/media" });
        RaiseAdded(new BoxSet { Id = Guid.NewGuid() });

        Assert.Empty(_journal.Read(cursor, "share", 10).ItemIds);
    }

    [Fact]
    public async Task Records_tracks_music_videos_home_videos_photos_and_books()
    {
        var cursor = await StartAsync();
        BaseItem[] items =
        [
            new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid(), Path = "/media/musica/a/1.flac" },
            new MusicVideo { Id = Guid.NewGuid(), Path = "/media/clip/a.mkv" },
            new Video { Id = Guid.NewGuid(), Path = "/media/casa/gita.mkv" },
            new Folder { Id = Guid.NewGuid(), Path = "/media/casa/Gita" },
            new Photo { Id = Guid.NewGuid(), Path = "/media/casa/Album/a.jpg" },
            new PhotoAlbum { Id = Guid.NewGuid(), Path = "/media/casa/Album" },
            new Book { Id = Guid.NewGuid(), Path = "/media/libri/a.epub" },
            new AudioBook { Id = Guid.NewGuid(), Path = "/media/libri/b.m4b" }
        ];

        foreach (var item in items)
        {
            RaiseAdded(item);
        }

        Assert.Equal(items.Select(item => item.Id), _journal.Read(cursor, "share", 10).ItemIds);
    }

    [Fact]
    public async Task Records_a_changed_album_so_that_its_tracks_reach_the_peer()
    {
        var cursor = await StartAsync();
        var album = new MediaBrowser.Controller.Entities.Audio.MusicAlbum { Id = Guid.NewGuid(), Path = "/media/musica/a" };

        RaiseUpdated(album);

        Assert.Equal([album.Id], _journal.Read(cursor, "share", 10).ItemIds);
    }

    [Fact]
    public async Task An_album_stub_never_enters_the_feed()
    {
        var cursor = await StartAsync();

        RaiseUpdated(new MediaBrowser.Controller.Entities.Audio.MusicAlbum { Id = Guid.NewGuid(), Path = "/vecchia/0f8fad5b-d9cb-469f-a165-70867728950e/7c9e6679742540de944be07fc1f90ae7/Musica/A/Album" });

        Assert.Empty(_journal.Read(cursor, "share", 10).ItemIds);
    }

    [Fact]
    public async Task A_series_of_an_old_0_5_folder_never_enters_the_feed()
    {
        _fixture.Store.Configuration.FederatedFolderPath = "/nuova";
        var cursor = await StartAsync();
        var real = new Series { Id = Guid.NewGuid(), Path = "/media/serie/Show" };

        RaiseUpdated(new Series { Id = Guid.NewGuid(), Path = "/vecchia/0f8fad5b-d9cb-469f-a165-70867728950e/Serie/Show" });
        RaiseUpdated(real);

        Assert.Equal([real.Id], _journal.Read(cursor, "share", 10).ItemIds);
    }

    [Fact]
    public async Task A_track_stub_never_enters_the_feed()
    {
        var cursor = await StartAsync();

        RaiseAdded(new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid(), Path = "/federata/0f8fad5b-d9cb-469f-a165-70867728950e/7c9e6679742540de944be07fc1f90ae7/Musica/A/1.strm" });

        Assert.Empty(_journal.Read(cursor, "share", 10).ItemIds);
    }

    // A peer reads an item through the catalog, which leaves federated items out, so recording one more is safe.
    [Fact]
    public async Task While_the_settings_cannot_be_read_a_change_is_still_recorded()
    {
        var cursor = await StartAsync();
        _fixture.Store.Unavailable = true;
        var movie = new Movie { Id = Guid.NewGuid(), Path = "/media/a.mkv" };

        RaiseAdded(movie);

        Assert.Equal([movie.Id], _journal.Read(cursor, "share", 10).ItemIds);
    }

    [Fact]
    public async Task After_stopping_nothing_is_recorded()
    {
        var recorder = new ChangeFeedRecorder(_fixture.Library.Object, _fixture.Catalog, _journal);
        await recorder.StartAsync(default);
        await recorder.StopAsync(default);
        var cursor = _journal.Read(null, "share", 10).Cursor;

        RaiseAdded(new Movie { Id = Guid.NewGuid(), Path = "/media/a.mkv" });

        Assert.Empty(_journal.Read(cursor, "share", 10).ItemIds);
    }
}
