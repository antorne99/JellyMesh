using System;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

/// <summary>Modalità: which plugin libraries each library mode wants for a server, and with which paths.</summary>
public sealed class LibraryPlanTests
{
    private const string Root = "/federata";
    private const string Films = "a1000000000000000000000000000001";
    private const string Films4K = "a1000000000000000000000000000002";
    private const string Shows = "b2000000000000000000000000000001";
    private const string Music = "c3000000000000000000000000000002";
    private const string MusicVideos = "c3000000000000000000000000000003";
    private const string HomeVideos = "c3000000000000000000000000000004";
    private const string Books = "c3000000000000000000000000000005";
    private const string Mixed = "d4000000000000000000000000000001";
    private static readonly Guid _serverId = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000001");

    private static RemoteServerConfig Server(LibraryMode mode, params (string Id, string Name, string Type)[] libraries)
    {
        var server = new RemoteServerConfig { Id = _serverId, Name = "Casa", LibraryMode = mode };
        foreach (var (id, name, type) in libraries)
        {
            server.RemoteLibraries.Add(new RemoteLibraryConfig { Id = id, Name = name, CollectionType = type });
            server.ImportedLibraryIds.Add(id);
        }

        return server;
    }

    private static string Folder(string library, string type) => $"{Root}/{_serverId:D}/{library}/{type}";

    [Fact]
    public void PerTypeHoldsTheFolderOfThatTypeOfEveryImportedLibraryInOneLibraryPerType()
    {
        var plan = LibraryPlan.For(Server(LibraryMode.ByType, (Films, "Film", "movies"), (Shows, "Serie TV", "tvshows"), (Films4K, "Film 4K", "movies")), Root, olderLayout: false);

        Assert.Equal(
            [
                ("Movies", ServerLibraryKind.Movies, string.Empty, "Film", new[] { Folder(Films, "Film"), Folder(Films4K, "Film") }),
                ("Series", ServerLibraryKind.Series, string.Empty, "Serie", new[] { Folder(Shows, "Serie") })
            ],
            plan.Select(library => (library.Key, library.Kind, library.RemoteLibraryId, library.Suffix, library.Paths.ToArray())));
    }

    [Fact]
    public void PerTypeMakesNoLibraryForATypeWithoutAnImportedLibrary()
    {
        var plan = LibraryPlan.For(Server(LibraryMode.ByType, (Films, "Film", "movies")), Root, olderLayout: false);

        Assert.Equal([ServerLibraryKind.Movies], plan.Select(library => library.Kind));
    }

    [Fact]
    public void PerRemoteLibraryMakesOneLibraryForEachImportedLibraryWithItsNameAndType()
    {
        var plan = LibraryPlan.For(Server(LibraryMode.ByRemoteLibrary, (Films, "Film", "movies"), (Shows, "Serie TV", "tvshows"), (Films4K, "Film 4K", "movies")), Root, olderLayout: false);

        Assert.Equal(
            [
                (Films, ServerLibraryKind.Movies, Films, "Film", new[] { Folder(Films, "Film") }),
                (Shows, ServerLibraryKind.Series, Shows, "Serie TV", new[] { Folder(Shows, "Serie") }),
                (Films4K, ServerLibraryKind.Movies, Films4K, "Film 4K", new[] { Folder(Films4K, "Film") })
            ],
            plan.Select(library => (library.Key, library.Kind, library.RemoteLibraryId, library.Suffix, library.Paths.ToArray())));
    }

    [Fact]
    public void PerTypeMakesALibraryForEachNewTypePresentWithTheNameOfItsTypeFolder()
    {
        var plan = LibraryPlan.For(
            Server(LibraryMode.ByType, (Books, "Libri", "books"), (Music, "Musica", "music"), (HomeVideos, "Casa", "homevideos"), (MusicVideos, "Clip", "musicvideos"), (Films, "Film", "movies")),
            Root,
            olderLayout: false);

        Assert.Equal(
            [
                ("Movies", ServerLibraryKind.Movies, "Film", new[] { Folder(Films, "Film") }),
                ("Music", ServerLibraryKind.Music, "Musica", new[] { Folder(Music, "Musica") }),
                ("MusicVideos", ServerLibraryKind.MusicVideos, "Video musicali", new[] { Folder(MusicVideos, "Video musicali") }),
                ("HomeVideos", ServerLibraryKind.HomeVideos, "Video domestici", new[] { Folder(HomeVideos, "Video domestici") }),
                ("Books", ServerLibraryKind.Books, "Libri", new[] { Folder(Books, "Libri") })
            ],
            plan.Select(library => (library.Key, library.Kind, library.Suffix, library.Paths.ToArray())));
    }

    [Fact]
    public void PerTypePutsTheFilmAndSerieFoldersOfAMixedLibraryInFilmAndSerie()
    {
        var plan = LibraryPlan.For(Server(LibraryMode.ByType, (Films, "Film", "movies"), (Mixed, "Tutto", "mixed")), Root, olderLayout: false);

        Assert.Equal(
            [
                (ServerLibraryKind.Movies, new[] { Folder(Films, "Film"), Folder(Mixed, "Film") }),
                (ServerLibraryKind.Series, new[] { Folder(Mixed, "Serie") })
            ],
            plan.Select(library => (library.Kind, library.Paths.ToArray())));
    }

    [Fact]
    public void PerRemoteLibraryMakesOneMixedLibraryOnAllTheTypeFoldersOfAMixedLibrary()
    {
        var plan = LibraryPlan.For(Server(LibraryMode.ByRemoteLibrary, (Mixed, "Tutto", "mixed"), (Music, "Musica", "music")), Root, olderLayout: false);

        Assert.Equal(
            [
                (Mixed, ServerLibraryKind.Mixed, Mixed, "Tutto", new[] { Folder(Mixed, "Film"), Folder(Mixed, "Serie") }),
                (Music, ServerLibraryKind.Music, Music, "Musica", new[] { Folder(Music, "Musica") })
            ],
            plan.Select(library => (library.Key, library.Kind, library.RemoteLibraryId, library.Suffix, library.Paths.ToArray())));
    }

    [Fact]
    public void InTheOlderLayoutNewTypesWaitForTheMigrationAndAMixedLibraryCountsAsFilmAndSerie()
    {
        var plan = LibraryPlan.For(Server(LibraryMode.ByType, (Music, "Musica", "music"), (Mixed, "Tutto", "mixed")), Root, olderLayout: true);

        Assert.Equal(
            [
                (ServerLibraryKind.Movies, new[] { $"{Root}/{_serverId:D}/Film" }),
                (ServerLibraryKind.Series, new[] { $"{Root}/{_serverId:D}/Serie" })
            ],
            plan.Select(library => (library.Kind, library.Paths.ToArray())));
    }

    [Fact]
    public void TheTypeFoldersOfAMixedLibraryAreFilmAndSerieAndANewTypeHasItsOwn()
    {
        Assert.Equal(
            [$"{_serverId:D}/{Mixed}/Film", $"{_serverId:D}/{Mixed}/Serie", $"{_serverId:D}/{Books}/Libri"],
            LibraryPlan.TypeFolders(Server(LibraryMode.None, (Mixed, "Tutto", "mixed"), (Books, "Libri", "books"))));
    }

    [Fact]
    public void TheManualPathsOfANewTypeHaveItsLocalType()
    {
        var paths = LibraryPlan.ManualPaths(Server(LibraryMode.None, (HomeVideos, "Casa", "homevideos"), (Music, "Musica", "music")), Root, olderLayout: false);

        Assert.Equal(
            [(Folder(HomeVideos, "Video domestici"), "homevideos", "Casa"), (Folder(Music, "Musica"), "music", "Musica")],
            paths.Select(path => (path.Path, path.LibraryType, path.RemoteLibrary)));
    }

    [Fact]
    public void TheManualPathsOfAMixedLibraryAreItsFilmFolderForFilmsAndItsSerieFolderForSeries()
    {
        var paths = LibraryPlan.ManualPaths(Server(LibraryMode.None, (Mixed, "Tutto", "mixed")), Root, olderLayout: false);

        Assert.Equal(
            [(Folder(Mixed, "Film"), "movies", "Tutto"), (Folder(Mixed, "Serie"), "tvshows", "Tutto")],
            paths.Select(path => (path.Path, path.LibraryType, path.RemoteLibrary)));
    }

    [Fact]
    public void NoneMakesNoLibrary()
    {
        Assert.Empty(LibraryPlan.For(Server(LibraryMode.None, (Films, "Film", "movies")), Root, olderLayout: false));
    }

    [Fact]
    public void OnlyImportedLibrariesOfAKnownTypeCount()
    {
        var server = Server(LibraryMode.ByRemoteLibrary, (Films, "Film", "movies"), (Shows, "Collezioni", "boxsets"));
        server.RemoteLibraries.Add(new RemoteLibraryConfig { Id = Films4K, Name = "Film 4K", CollectionType = "movies" });
        server.ImportedLibraryIds.Add("c3000000000000000000000000000001");

        Assert.Equal([Films], LibraryPlan.For(server, Root, olderLayout: false).Select(library => library.Key));
    }

    [Fact]
    public void InTheOlderLayoutPerTypeHoldsTheFolderOfTheServerForTheTypesPresent()
    {
        var plan = LibraryPlan.For(Server(LibraryMode.ByType, (Shows, "Serie TV", "tvshows")), Root, olderLayout: true);

        var library = Assert.Single(plan);
        Assert.Equal("Series", library.Key);
        Assert.Equal([$"{Root}/{_serverId:D}/Serie"], library.Paths);
    }

    [Fact]
    public void InTheOlderLayoutPerRemoteLibraryWaitsForTheMigration()
    {
        Assert.Empty(LibraryPlan.For(Server(LibraryMode.ByRemoteLibrary, (Films, "Film", "movies")), Root, olderLayout: true));
    }

    [Fact]
    public void TheManualPathsOfAServerAreTheTypeFoldersOfItsImportedLibraries()
    {
        var paths = LibraryPlan.ManualPaths(Server(LibraryMode.None, (Films, "Film", "movies"), (Shows, "Serie TV", "tvshows")), Root, olderLayout: false);

        Assert.Equal(
            [(Folder(Films, "Film"), "movies", "Film"), (Folder(Shows, "Serie"), "tvshows", "Serie TV")],
            paths.Select(path => (path.Path, path.LibraryType, path.RemoteLibrary)));
    }

    [Fact]
    public void InTheOlderLayoutTheManualPathsAreTheFoldersOfTheServerForTheTypesPresent()
    {
        var paths = LibraryPlan.ManualPaths(Server(LibraryMode.None, (Films, "Film", "movies"), (Films4K, "Film 4K", "movies")), Root, olderLayout: true);

        Assert.Equal([($"{Root}/{_serverId:D}/Film", "movies")], paths.Select(path => (path.Path, path.LibraryType)));
    }

    [Fact]
    public void TheTypeFoldersOfAServerAreThoseOfItsImportedLibraries()
    {
        Assert.Equal(
            [$"{_serverId:D}/{Films}/Film", $"{_serverId:D}/{Shows}/Serie"],
            LibraryPlan.TypeFolders(Server(LibraryMode.None, (Films, "Film", "movies"), (Shows, "Serie TV", "tvshows"))));
    }

    [Fact]
    public void ALibraryIdThatIsNotAJellyfinIdNeverBecomesAFolder()
    {
        var server = Server(LibraryMode.ByRemoteLibrary, (Films, "Film", "movies"), ("../escape", "Altro", "movies"));

        Assert.Equal([Films], LibraryPlan.For(server, Root, olderLayout: false).Select(planned => planned.RemoteLibraryId));
        Assert.Single(LibraryPlan.TypeFolders(server));
        Assert.Single(LibraryPlan.ManualPaths(server, Root, olderLayout: false));
    }

    [Fact]
    public void ARemoteLibraryKeptTwiceGivesOneLibraryAndOneFolder()
    {
        var server = Server(LibraryMode.ByRemoteLibrary, (Films, "Film", "movies"));
        server.RemoteLibraries.Add(new RemoteLibraryConfig { Id = Films, Name = "Film bis", CollectionType = "movies" });

        Assert.Equal([Folder(Films, "Film")], Assert.Single(LibraryPlan.For(server, Root, olderLayout: false)).Paths);
        server.LibraryMode = LibraryMode.ByType;
        Assert.Equal([Folder(Films, "Film")], Assert.Single(LibraryPlan.For(server, Root, olderLayout: false)).Paths);
        Assert.Single(LibraryPlan.TypeFolders(server));
    }
}
