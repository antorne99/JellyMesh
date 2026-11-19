using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// Migrazione delle cartelle (spec F44, §10 rows 444-445): the titles of the flat folders and of the 0.5 server folders go
// to the remote library layout, and the admin's libraries get the type folders in place of the old folders.
public sealed partial class LayoutMigrationTests
{
    [Fact]
    public async Task TheTitlesOfTheServerFoldersGoToTheTypeFoldersAndTheServerFoldersLeaveTheLibraries()
    {
        BothServersImport();
        var old = ServerFolderFilm(_serverA, "a1");
        var a = NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        Row(_mario, old).IsFavorite = true;
        var migration = await MigrationAsync();

        await migration.Choose(LayoutMigrationMode.Automatic).Job;

        Assert.True(Row(_mario, a).IsFavorite);
        Assert.Equal([NewFilms(_serverA), NewFilms(_serverB)], Locations("Film"));
        Assert.All(_registry.State.Stubs, stub => Assert.NotNull(FederatedPaths.RemoteLibraryOf(stub.RelativePath)));
        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
        Assert.Null(migration.GetStatus(Root));
    }

    [Fact]
    public async Task AServerFolderInALibraryNeedsOnlyTheTypeFoldersOfItsServer()
    {
        BothServersImport();
        ServerFolderFilm(_serverA, "a1");
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film di A", CollectionTypeOptions.movies, ServerFilms(_serverA)));

        var status = (await MigrationAsync()).GetStatus(Root)!;

        var library = Assert.Single(status.Libraries);
        Assert.Equal([ServerFilms(_serverA)], library.OldPaths);
        Assert.Equal([NewFilms(_serverA)], library.MissingPaths);
        Assert.Equal([NewFilms(_serverB)], status.UnlinkedFolders);
        Assert.Equal(1, status.OldTitles);
    }

    [Fact]
    public async Task TitlesOfBothOlderLayoutsGoToTheTypeFolders()
    {
        // A 0.5 install whose flat titles were not all carried yet.
        BothServersImport();
        var (_, flat) = OldFilm("Dune", (_serverA, "a1"));
        var server = ServerFolderFilm(_serverA, "a2");
        var dune = NewFilm(_serverA, "a1");
        var other = NewFilm(_serverA, "a2");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms, ServerFilms(_serverA)));
        Row(_mario, flat).IsFavorite = true;
        Row(_luigi, server).IsFavorite = true;

        await (await MigrationAsync()).Choose(LayoutMigrationMode.Automatic).Job;

        Assert.True(Row(_mario, dune).IsFavorite);
        Assert.True(Row(_luigi, other).IsFavorite);
        Assert.Equal([NewFilms(_serverA), NewFilms(_serverB)], Locations("Film"));
        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
    }

    [Fact]
    public async Task ALibraryThePluginKeepsIsLeftToIt()
    {
        // The plugin moves its own libraries to the type folders itself (§10 row 445).
        BothServersImport();
        var a = _store.Configuration.Servers.Single(server => server.Id == _serverA);
        a.LibraryMode = LibraryMode.ByType;
        a.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "A - Film", ItemId = IdOf("A - Film") });
        ServerFolderFilm(_serverA, "a1");
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("A - Film", CollectionTypeOptions.movies, ServerFilms(_serverA)));
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverB)));

        var status = (await MigrationAsync()).GetStatus(Root)!;

        var library = Assert.Single(status.Libraries);
        Assert.Equal("Film", library.Name);
        Assert.Equal([NewFilms(_serverB)], library.MissingPaths);
        Assert.Empty(status.UnlinkedFolders);
    }

    [Fact]
    public async Task ALibraryThePluginKeepsIsOnTheTypeFoldersWhenTheMigrationEnds()
    {
        BothServersImport();
        var a = _store.Configuration.Servers.Single(server => server.Id == _serverA);
        a.LibraryMode = LibraryMode.ByType;
        a.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "A - Film", ItemId = IdOf("A - Film") });
        var old = ServerFolderFilm(_serverA, "a1");
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("A - Film", CollectionTypeOptions.movies, ServerFilms(_serverA)));
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        Row(_mario, old).IsFavorite = true;

        await (await MigrationAsync()).Choose(LayoutMigrationMode.Automatic).Job;

        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
        Assert.Equal([NewFilms(_serverA)], Locations("A - Film"));
    }

    [Fact]
    public async Task APluginLibraryCheckThatFailsAtTheEndLeavesTheMigrationDone()
    {
        BothServersImport();
        ServerFolderFilm(_serverA, "a1");
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        var manager = new Mock<IServerLibraryManager>();
        manager.Setup(libraries => libraries.FollowForPlacement()).Returns(() => new PlacementLibraries(_libraries.ToList(), new HashSet<VirtualFolderInfo>()));
        manager.Setup(libraries => libraries.ScanAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        manager.Setup(libraries => libraries.ReconcileAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        _managerOverride = manager.Object;

        await (await MigrationAsync()).Choose(LayoutMigrationMode.Automatic).Job;

        Assert.Equal((AutomaticMigrationStep.Done, AutomaticMigrationError.None), (_store.Configuration.LayoutMigration.Step, _store.Configuration.LayoutMigration.Error));
    }

    [Fact]
    public async Task AStubFileLeftInAServerFolderStopsTheMigrationBeforeTheRemoval()
    {
        BothServersImport();
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms, ServerFilms(_serverA)));
        var stray = Path.Combine(ServerFilms(_serverA), "Dune", "Dune.strm");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        await File.WriteAllTextAsync(stray, "x");
        var migration = await MigrationAsync();

        await migration.Choose(LayoutMigrationMode.Automatic).Job;

        Assert.Equal(AutomaticMigrationError.OldStubsLeft, _store.Configuration.LayoutMigration.Error);
        Assert.Contains(ServerFilms(_serverA), Locations("Film"));
        Assert.True(File.Exists(stray));
    }

    [Fact]
    public async Task TheImportedLibrariesNotReadYetStayToPlaceAfterTheMigration()
    {
        BothServersImport();
        var a = _store.Configuration.Servers.Single(server => server.Id == _serverA);
        var unread = NewId();
        a.ImportedLibraryIds.Add(unread);
        a.LibrariesToPlace.Add(TestLibraries.Films);
        OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));

        await (await MigrationAsync()).Choose(LayoutMigrationMode.Automatic).Job;

        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
        Assert.Equal([unread], a.LibrariesToPlace);
    }

    // §10 row 445 [D19]: the placement of a title in two remote libraries is row 439, so the tests give its stubs.
    [Fact]
    public async Task ATitleInTwoRemoteLibrariesInDifferentLocalLibrariesGetsTheStateOnBothStubs()
    {
        BothServersImport();
        var (_, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var films = NewFilm(_serverA, "a1");
        var other = NewFilmIn(_serverA, TestLibraries.FilmsB, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, NewFilms(_serverA), NewFilms(_serverB)));
        _libraries.Add(Library("Altri film", CollectionTypeOptions.movies, TypeFolderOf(_serverA, TestLibraries.FilmsB, TypeFolder.Movies)));
        var row = Row(_mario, oldItem);
        row.PlayCount = 1;
        row.Played = true;
        row.IsFavorite = true;

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.All([films, other], item => Assert.Equal((1, true, true), (Row(_mario, item).PlayCount, Row(_mario, item).Played, Row(_mario, item).IsFavorite)));
        Assert.DoesNotContain(_registry.State.Stubs, stub => stub.ServerId is null);
    }

    [Fact]
    public async Task ATitleInTwoRemoteLibrariesInTheSameLocalLibraryGetsTheStateOnce()
    {
        // One stub with the second remote library as an internal source.
        BothServersImport();
        var (_, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var films = NewFilm(_serverA, "a1");
        var stub = _stubs.Single(entry => entry.ServerId == _serverA);
        _stubs[_stubs.IndexOf(stub)] = stub with { Sources = [.. stub.Sources, Source((_serverA, "a1")) with { LibraryId = TestLibraries.FilmsB }] };
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms, NewFilms(_serverA), NewFilms(_serverB)));
        var row = Row(_mario, oldItem);
        row.PlayCount = 1;
        row.Played = true;

        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);
        await migration.RunAsync(CancellationToken.None);

        Assert.Equal((1, true), (Row(_mario, films).PlayCount, Row(_mario, films).Played));
        Assert.DoesNotContain(_registry.State.Stubs, entry => entry.ServerId is null);
    }

    // A film of a server in the type folder of the given remote library.
    private Movie NewFilmIn(Guid server, string library, string itemId)
    {
        var name = Id();
        var stub = new StubEntry
        {
            Id = name,
            ServerId = server,
            Kind = StubKind.Movie,
            RelativePath = Path.Combine(FederatedPaths.TypeFolderOf(server, library, TypeFolder.Movies), name, name + ".strm"),
            IdentityKey = "new:" + itemId,
            Sources = [Source((server, itemId)) with { LibraryId = library }]
        };
        _stubs.Add(stub);
        return Add(new Movie { Path = FederatedPaths.Resolve(Root, stub.RelativePath) });
    }
}
