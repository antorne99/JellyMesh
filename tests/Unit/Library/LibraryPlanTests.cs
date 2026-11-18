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
    public void NoneMakesNoLibrary()
    {
        Assert.Empty(LibraryPlan.For(Server(LibraryMode.None, (Films, "Film", "movies")), Root, olderLayout: false));
    }

    [Fact]
    public void OnlyImportedLibrariesOfAKnownTypeCount()
    {
        var server = Server(LibraryMode.ByRemoteLibrary, (Films, "Film", "movies"), (Shows, "Musica", "music"));
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
}
