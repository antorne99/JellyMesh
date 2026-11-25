using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The libraries of the new types and of the mixed remote libraries, with their names and options (§3.12, D14, D15).
public sealed partial class ServerLibraryManagerTests
{
    private static readonly string _musicFolder = FederatedPaths.TypeFolderOf(_serverId, TestLibraries.Music, TypeFolder.Music);
    private static readonly string _mixedFilmFolder = FederatedPaths.TypeFolderOf(_serverId, TestLibraries.Mixed, TypeFolder.Movies);
    private static readonly string _mixedSerieFolder = FederatedPaths.TypeFolderOf(_serverId, TestLibraries.Mixed, TypeFolder.Series);

    // A server out of the 0.5 layout that also imports a library of each new type.
    private static Fixture WithNewTypes()
    {
        var fixture = OnTypeFolders();
        Import(fixture.Server, TestLibraries.Music, "Dischi", "music");
        Import(fixture.Server, TestLibraries.HomeVideos, "Ricordi", "homevideos");
        Import(fixture.Server, TestLibraries.Books, "Biblioteca", "books");
        Import(fixture.Server, TestLibraries.MusicVideos, "Concerti", "musicvideos");
        return fixture;
    }

    // A server out of the 0.5 layout that also imports a library with no type.
    private static Fixture WithMixed()
    {
        var fixture = OnTypeFolders();
        Import(fixture.Server, TestLibraries.Mixed, "Tutto", "mixed");
        return fixture;
    }

    // The libraries made when each block was written, in the order of the blocks.
    private static List<List<string>> TrackMadeAtBlocks(Fixture fixture)
    {
        var made = new List<List<string>>();
        fixture.Renamer.OnBlock = () => made.Add([.. fixture.Libraries.Created.Select(call => call.Name)]);
        return made;
    }

    // The carries of the blocks written for who blocks a library, with the index of their block.
    private static List<(int Block, AccessCarry Carry)> BlocksOf(Fixture fixture, Guid source) =>
        [.. fixture.Renamer.Blocked.SelectMany((carries, block) => carries.Select(carry => (block, carry)))
            .Where(pair => pair.carry.Folders.SelectMany(ids => ids).Contains(source))];

    // The stubs are read and no type folder holds titles yet, as for the new types until their titles are written.
    private static void NoTitles(Fixture fixture)
        => fixture.Stubs.Setup(stubs => stubs.TypeFoldersOf(It.IsAny<Guid>(), It.IsAny<TypeFolder>())).Returns([]);

    // Each library of the server with its type and paths, by name.
    private static List<(string Name, CollectionTypeOptions? Type, string Paths)> Snapshot(Fixture fixture) =>
        [.. fixture.Server.AutomaticLibraryRegistry
            .Select(record => fixture.Libraries.Find(record.Name))
            .Select(folder => (folder.Name, folder.CollectionType, string.Join('|', folder.Locations)))
            .OrderBy(library => library.Name, StringComparer.Ordinal)];

    private static void Import(RemoteServerConfig server, string id, string name, string type)
    {
        server.RemoteLibraries.Add(new RemoteLibraryConfig { Id = id, Name = name, CollectionType = type });
        server.ImportedLibraryIds.Add(id);
    }

    [Fact]
    public async Task By_type_creates_a_library_for_each_new_type_present_with_the_name_of_its_type()
    {
        var fixture = WithNewTypes();

        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        Assert.Equal(
            [
                ("Casa - Film", CollectionTypeOptions.movies),
                ("Casa - Libri", CollectionTypeOptions.books),
                ("Casa - Musica", CollectionTypeOptions.music),
                ("Casa - Serie", CollectionTypeOptions.tvshows),
                ("Casa - Video domestici", CollectionTypeOptions.homevideos),
                ("Casa - Video musicali", CollectionTypeOptions.musicvideos)
            ],
            fixture.Libraries.Created.Select(call => (call.Name, call.Type!.Value)).OrderBy(call => call.Name, StringComparer.Ordinal));
        Assert.Equal([Absolute(_musicFolder)], fixture.Libraries.Created.Single(call => call.Name == "Casa - Musica").Options.PathInfos.Select(info => info.Path));
        Assert.Single(fixture.Scans.Steps, step => step == "scan");
    }

    [Fact]
    public async Task By_remote_library_names_a_library_of_a_new_type_after_its_remote_library()
    {
        var fixture = WithNewTypes();

        await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;

        var music = Assert.Single(fixture.Libraries.Created, call => call.Type == CollectionTypeOptions.music);
        Assert.Equal(("Casa - Dischi", Absolute(_musicFolder)), (music.Name, Assert.Single(music.Options.PathInfos).Path));
        Assert.Equal(7, fixture.Libraries.Created.Count);
    }

    [Fact]
    public async Task By_remote_library_makes_one_library_without_a_type_on_the_film_and_serie_folders_of_a_mixed_library()
    {
        var fixture = WithMixed();

        await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        await fixture.NewManager().ReconcileAsync(CancellationToken.None);

        var mixed = Assert.Single(fixture.Libraries.Created, call => call.Name == "Casa - Tutto");
        Assert.Null(mixed.Type);
        Assert.Equal([Absolute(_mixedFilmFolder), Absolute(_mixedSerieFolder)], mixed.Options.PathInfos.Select(info => info.Path));
        Assert.Equal(4, fixture.Libraries.Created.Count);
        var record = Assert.Single(fixture.Server.AutomaticLibraryRegistry, record => record.RemoteLibraryId == TestLibraries.Mixed);
        Assert.Equal((ServerLibraryKind.Mixed, fixture.Libraries.IdOf("Casa - Tutto"), false), (record.Kind, record.ItemId, record.Adopted));
    }

    [Fact]
    public async Task By_type_puts_the_films_and_series_of_a_mixed_library_in_Film_and_Serie()
    {
        var fixture = WithMixed();

        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        Assert.Equal(
            [
                ("Casa - Film", new[] { Absolute(_filmsFolder), Absolute(_filmsBFolder), Absolute(_mixedFilmFolder) }),
                ("Casa - Serie", new[] { Absolute(_showsFolder), Absolute(_mixedSerieFolder) })
            ],
            fixture.Libraries.Created.Select(call => (call.Name, call.Options.PathInfos.Select(info => info.Path).ToArray())).OrderBy(call => call.Name, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(CollectionTypeOptions.mixed)]
    public async Task An_admin_library_without_a_type_on_exactly_the_folders_of_a_mixed_library_is_adopted(CollectionTypeOptions? type)
    {
        var fixture = WithMixed();
        fixture.Libraries.Add("Tutto mio", type, [Absolute(_mixedFilmFolder), Absolute(_mixedSerieFolder)]);

        await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;

        Assert.DoesNotContain(fixture.Libraries.Created, call => call.Name == "Casa - Tutto");
        var adopted = Assert.Single(fixture.Server.AutomaticLibraryRegistry, record => record.Adopted);
        Assert.Equal(("Tutto mio", ServerLibraryKind.Mixed), (adopted.Name, adopted.Kind));
        Assert.Empty(fixture.Manager.GetState(_serverId).Conflicts);
    }

    [Fact]
    public async Task A_library_without_a_type_on_the_film_folders_is_another_type_for_the_Film_library()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        fixture.Libraries.Add("Senza tipo", null, FilmPaths);

        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        Assert.Empty(fixture.Libraries.Created);
        Assert.Equal(("Senza tipo", LibraryConflictReason.OtherType), Assert.Single(fixture.Manager.GetState(_serverId).Conflicts) is var conflict ? (conflict.Library, conflict.Reason) : default);
    }

    [Fact]
    public async Task A_library_of_a_new_type_and_a_mixed_library_are_renamed_with_their_types()
    {
        var fixture = WithMixed();
        Import(fixture.Server, TestLibraries.Music, "Dischi", "music");
        await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;
        fixture.Server.Name = "Mare";

        Assert.True(await fixture.Manager.RenameAsync(_serverId, CancellationToken.None));

        Assert.Contains(("Casa - Tutto", (Jellyfin.Data.Enums.CollectionType?)null), fixture.Renamer.Types);
        Assert.Contains(("Casa - Dischi", (Jellyfin.Data.Enums.CollectionType?)Jellyfin.Data.Enums.CollectionType.music), fixture.Renamer.Types);
        Assert.Contains("Mare - Tutto", fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name));
    }

    [Fact]
    public async Task The_libraries_of_the_new_types_save_no_lyrics_download_no_subtitles_and_show_photos_only_with_home_videos()
    {
        var fixture = WithNewTypes();

        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        var created = fixture.Libraries.Created.Where(call => call.Type is not (CollectionTypeOptions.movies or CollectionTypeOptions.tvshows)).ToList();
        Assert.Equal(4, created.Count);
        Assert.All(created, call =>
        {
            Assert.False(call.Options.SaveLyricsWithMedia);
            Assert.Empty(call.Options.SubtitleDownloadLanguages!);
            Assert.False(call.Options.EnableLUFSScan);
            Assert.False(call.Options.EnableRealtimeMonitor);
            Assert.False(call.Options.SaveLocalMetadata);
            Assert.Empty(call.Options.MetadataSavers!);
            Assert.Empty(call.Options.TypeOptions!);
            Assert.Equal(call.Type == CollectionTypeOptions.homevideos, call.Options.EnablePhotos);
        });
    }

    [Fact]
    public async Task The_Film_Serie_and_mixed_libraries_keep_the_options_of_the_film_and_series_libraries()
    {
        var fixture = WithMixed();

        await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;

        Assert.All(fixture.Libraries.Created, call =>
        {
            Assert.Null(call.Options.SubtitleDownloadLanguages);
            Assert.True(call.Options.EnablePhotos);
            Assert.False(call.Options.EnableLUFSScan);
            Assert.False(call.Options.EnableRealtimeMonitor);
        });
    }

    [Fact]
    public async Task A_shared_library_of_a_new_type_is_made_only_once_a_server_has_that_type()
    {
        var fixture = OnTypeFolders();
        await fixture.Manager.EnableShared().Job;
        Assert.Equal([SharedFilm, SharedSeries], fixture.Libraries.Created.Select(call => call.Name).Order(StringComparer.Ordinal));

        Import(fixture.Server, TestLibraries.Music, "Dischi", "music");
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        var music = Assert.Single(fixture.Libraries.Created, call => call.Name == "Tutti i server - Musica");
        Assert.Equal((CollectionTypeOptions.music, Absolute(_musicFolder)), (music.Type!.Value, Assert.Single(music.Options.PathInfos).Path));
        Assert.Equal(3, fixture.Libraries.Created.Count);
    }

    [Fact]
    public async Task Switching_on_makes_a_shared_library_for_each_new_type_present_with_its_options()
    {
        var fixture = WithNewTypes();

        await fixture.Manager.EnableShared().Job;

        Assert.Equal(
            [
                (SharedFilm, CollectionTypeOptions.movies),
                ("Tutti i server - Libri", CollectionTypeOptions.books),
                ("Tutti i server - Musica", CollectionTypeOptions.music),
                (SharedSeries, CollectionTypeOptions.tvshows),
                ("Tutti i server - Video domestici", CollectionTypeOptions.homevideos),
                ("Tutti i server - Video musicali", CollectionTypeOptions.musicvideos)
            ],
            fixture.Libraries.Created.Select(call => (call.Name, call.Type!.Value)).OrderBy(call => call.Name, StringComparer.Ordinal));
        Assert.True(fixture.Libraries.Created.Single(call => call.Type == CollectionTypeOptions.homevideos).Options.EnablePhotos);
        Assert.False(fixture.Libraries.Created.Single(call => call.Type == CollectionTypeOptions.music).Options.EnablePhotos);
        Assert.Single(fixture.Scans.Steps, step => step == "scan");
    }

    [Fact]
    public async Task The_shared_Film_and_Serie_take_the_film_and_serie_folders_of_a_mixed_library()
    {
        var fixture = WithMixed();

        await fixture.Manager.EnableShared().Job;

        Assert.Equal([SharedFilm, SharedSeries], fixture.Libraries.Created.Select(call => call.Name).Order(StringComparer.Ordinal));
        Assert.Contains(Absolute(_mixedFilmFolder), fixture.Libraries.Created.Single(call => call.Name == SharedFilm).Options.PathInfos.Select(info => info.Path));
        Assert.Contains(Absolute(_mixedSerieFolder), fixture.Libraries.Created.Single(call => call.Name == SharedSeries).Options.PathInfos.Select(info => info.Path));
    }

    [Fact]
    public async Task Before_they_exist_the_shared_Film_and_Serie_are_blocked_for_who_blocks_the_library_of_a_mixed_library()
    {
        var fixture = WithMixed();
        await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;
        var mixed = Guid.Parse(fixture.Libraries.IdOf("Casa - Tutto"));
        var madeWhenBlocked = TrackMadeAtBlocks(fixture);

        await fixture.Manager.EnableShared().Job;

        var carries = BlocksOf(fixture, mixed);
        Assert.Equal([SharedFilm, SharedSeries], carries.Select(carry => carry.Carry.Name).Order(StringComparer.Ordinal));
        Assert.All(carries, carry => Assert.DoesNotContain(carry.Carry.Name, madeWhenBlocked[carry.Block]));
    }

    [Fact]
    public async Task Before_it_exists_the_shared_library_of_a_new_type_is_blocked_for_who_blocks_a_library_on_its_folders()
    {
        var fixture = OnTypeFolders();
        Import(fixture.Server, TestLibraries.Music, "Dischi", "music");
        Import(fixture.Server, "a0d1000000000000000000000000000b", "Vinili", "music");
        fixture.Libraries.Add("Mia musica", CollectionTypeOptions.music, Absolute(_musicFolder));
        var madeWhenBlocked = TrackMadeAtBlocks(fixture);

        await fixture.Manager.EnableShared().Job;

        var (block, carry) = Assert.Single(BlocksOf(fixture, Guid.Parse(fixture.Libraries.IdOf("Mia musica"))));
        Assert.Equal("Tutti i server - Musica", carry.Name);
        Assert.DoesNotContain(carry.Name, madeWhenBlocked[block]);
        Assert.Contains(fixture.Libraries.Created, call => call.Name == "Tutti i server - Musica");
    }

    [Theory]
    [InlineData(LibraryMode.ByType, 3)]
    [InlineData(LibraryMode.ByRemoteLibrary, 2)]
    public async Task Libraries_of_a_new_type_and_of_a_mixed_library_with_no_titles_yet_stay_as_made(LibraryMode mode, int count)
    {
        var fixture = WithMixed();
        Import(fixture.Server, TestLibraries.Music, "Dischi", "music");
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Films);
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.FilmsB);
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        NoTitles(fixture);
        await fixture.Manager.Enable(_serverId, mode).Job;
        var made = Snapshot(fixture);

        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        await fixture.NewManager().ReconcileAsync(CancellationToken.None);

        Assert.Equal(count, made.Count);
        Assert.Equal(made, Snapshot(fixture));
        Assert.Equal(count, fixture.Libraries.Created.Count);
        Assert.Empty(fixture.Libraries.Removed);
        Assert.Empty(fixture.Libraries.RemovedPaths);
    }

    [Fact]
    public async Task A_shared_library_of_a_new_type_with_no_titles_yet_stays_as_made()
    {
        var fixture = WithMixed();
        Import(fixture.Server, TestLibraries.Music, "Dischi", "music");
        NoTitles(fixture);
        await fixture.Manager.EnableShared().Job;
        var made = Shared(fixture).Libraries.Select(record => (record.Name, string.Join('|', record.Paths))).ToList();

        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        await fixture.NewManager().ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Tutti i server - Film", "Tutti i server - Musica", "Tutti i server - Serie"], made.Select(record => record.Name).Order(StringComparer.Ordinal));
        Assert.Equal(made, Shared(fixture).Libraries.Select(record => (record.Name, string.Join('|', record.Paths))));
        Assert.Equal(3, fixture.Libraries.Created.Count);
        Assert.Empty(fixture.Libraries.Removed);
        Assert.Empty(fixture.Libraries.RemovedPaths);
    }

    [Fact]
    public async Task A_mixed_library_comes_and_goes_with_a_change_of_mode()
    {
        var fixture = WithMixed();
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        fixture.Server.LibraryMode = LibraryMode.ByRemoteLibrary;
        await fixture.Manager.ApplyMode(_serverId).Job;
        var mixed = fixture.Libraries.Find("Casa - Tutto");
        Assert.Null(mixed.CollectionType);
        Assert.Equal([Absolute(_mixedFilmFolder), Absolute(_mixedSerieFolder)], mixed.Locations);
        Assert.Equal(
            [TestLibraries.Shows, TestLibraries.Mixed, TestLibraries.Films, TestLibraries.FilmsB],
            fixture.Server.AutomaticLibraryRegistry.Select(record => record.RemoteLibraryId).Order(StringComparer.Ordinal));

        fixture.Server.LibraryMode = LibraryMode.ByType;
        await fixture.Manager.ApplyMode(_serverId).Job;
        Assert.DoesNotContain(fixture.Libraries.Folders, folder => folder.Name == "Casa - Tutto");
        Assert.Equal([ServerLibraryKind.Movies, ServerLibraryKind.Series], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Kind).Order());
        var series = fixture.Server.AutomaticLibraryRegistry.Single(record => record.Kind == ServerLibraryKind.Series);
        Assert.Equal([Absolute(_showsFolder), Absolute(_mixedSerieFolder)], fixture.Libraries.Find(series.Name).Locations);
    }
}
