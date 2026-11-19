using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// Servers linked after the automatic migration (Q58, D21, D23, D24): their folders join the libraries of the other
// servers, and the settings set the mode, the same field as the banner (§10 rows 77, 368).
public sealed partial class LayoutMigrationTests
{
    private static readonly Guid _serverC = Guid.NewGuid();
    private readonly Mock<ILibraryRenamer> _renamer = new();
    private ServerLibraryManager? _manager;

    // When set, the migration asks this manager instead of the real one.
    private IServerLibraryManager? _managerOverride;

    // A library rename left halfway, which keeps the manager busy.
    private bool _managerBusy;

    [Fact]
    public async Task AServerLinkedAfterTheMigrationJoinsEveryLibraryHoldingAFolderOfTheSameKindOfAnotherServer()
    {
        Migrated();
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, LocalFilms, ServerFilms(_serverA), ServerFilms(_serverB)));
        _libraries.Add(Library("Serie", CollectionTypeOptions.tvshows, ServerSeries(_serverA), ServerSeries(_serverB)));
        _libraries.Add(Library("Solo A", CollectionTypeOptions.mixed, ServerFilms(_serverA)));
        _libraries.Add(Library("Locale", CollectionTypeOptions.movies, LocalFilms));
        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([LocalFilms, ServerFilms(_serverA), ServerFilms(_serverB), ServerFilms(_serverC)], Locations("Film"));
        Assert.Equal([ServerSeries(_serverA), ServerSeries(_serverB), ServerSeries(_serverC)], Locations("Serie"));
        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverC)], Locations("Solo A"));
        Assert.Equal([LocalFilms], Locations("Locale"));
        Assert.Equal(1, _scansQueued);
        Assert.Contains(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
        _materializer.Verify(materializer => materializer.PrepareServerFolders(), Times.Once);
        Assert.Null(migration.GetStatus(Root));
    }

    [Fact]
    public async Task InTheRemoteLibraryLayoutANewServerJoinsWithOneTypeFolderPerImportedRemoteLibrary()
    {
        // §10 row 443: a library holding a 0.5 folder or a type folder of another server gets the type folders of that type.
        MigratedToRemoteLibraries();
        var (films, films4K, shows) = (Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var c = Imported(_serverC, (films, "movies"), (shows, "tvshows"), (films4K, "movies"));
        var otherFilms = Guid.NewGuid().ToString("N");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, LocalFilms, TypeFolderOf(_serverA, otherFilms, TypeFolder.Movies)));
        _libraries.Add(Library("Vecchio", CollectionTypeOptions.movies, ServerFilms(_serverB)));
        _libraries.Add(Library("Serie", CollectionTypeOptions.tvshows, TypeFolderOf(_serverA, otherFilms, TypeFolder.Series)));
        _libraries.Add(Library("Locale", CollectionTypeOptions.movies, LocalFilms));
        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([LocalFilms, TypeFolderOf(_serverA, otherFilms, TypeFolder.Movies), TypeFolderOf(_serverC, films, TypeFolder.Movies), TypeFolderOf(_serverC, films4K, TypeFolder.Movies)], Locations("Film"));
        Assert.Equal([ServerFilms(_serverB), TypeFolderOf(_serverC, films, TypeFolder.Movies), TypeFolderOf(_serverC, films4K, TypeFolder.Movies)], Locations("Vecchio"));
        Assert.Equal([TypeFolderOf(_serverA, otherFilms, TypeFolder.Series), TypeFolderOf(_serverC, shows, TypeFolder.Series)], Locations("Serie"));
        Assert.Equal([LocalFilms], Locations("Locale"));
        Assert.DoesNotContain(_addedPaths, added => added.Path == ServerFilms(_serverC) || added.Path == ServerSeries(_serverC));
        Assert.Equal(1, _scansQueued);
        Assert.Contains(c.Id, _store.Configuration.LayoutMigration.PlacedServers);
    }

    [Fact]
    public async Task ANewServerWhoseRemoteLibrariesAreNotReadYetWaitsForTheNextHook()
    {
        MigratedToRemoteLibraries();
        var films = Guid.NewGuid().ToString("N");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, TypeFolderOf(_serverA, Guid.NewGuid().ToString("N"), TypeFolder.Movies)));
        var c = Imported(_serverC);
        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Empty(_addedPaths);
        Assert.DoesNotContain(_serverC, _store.Configuration.LayoutMigration.PlacedServers);

        c.RemoteLibraries.Add(new RemoteLibraryConfig { Id = films, Name = "Film", CollectionType = "movies" });
        c.ImportedLibraryIds.Add(films);
        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([("Film", TypeFolderOf(_serverC, films, TypeFolder.Movies))], _addedPaths);
        Assert.Contains(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
    }

    [Fact]
    public async Task ANewServerWithNoImportedLibraryWaitsUntilTheAdminImportsOne()
    {
        // Its remote libraries were read, but none is imported now: it has no type folder yet, so it stays to place.
        MigratedToRemoteLibraries();
        var films = Guid.NewGuid().ToString("N");
        var c = Imported(_serverC, (films, "movies"));
        c.ImportedLibraryIds.Clear();
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, TypeFolderOf(_serverA, Guid.NewGuid().ToString("N"), TypeFolder.Movies)));
        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Empty(_addedPaths);
        Assert.Equal(0, _scansQueued);
        Assert.DoesNotContain(_serverC, _store.Configuration.LayoutMigration.PlacedServers);

        c.ImportedLibraryIds.Add(films);
        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([("Film", TypeFolderOf(_serverC, films, TypeFolder.Movies))], _addedPaths);
        Assert.Contains(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
    }

    [Fact]
    public async Task StubFilesLeftInTheServerFoldersKeepTheNewServerOnItsServerFolders()
    {
        // The registry is on the remote library layout, but the materializer keeps the older one while such files are left.
        MigratedToRemoteLibraries();
        var stub = FederatedPaths.Resolve(Root, FederatedPaths.MovieStub(_serverA, "Dune (2021)", "A"));
        Directory.CreateDirectory(Path.GetDirectoryName(stub)!);
        await File.WriteAllTextAsync(stub, "stub");
        Imported(_serverC, (Guid.NewGuid().ToString("N"), "movies"));
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA)));
        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([("Film", ServerFilms(_serverC))], _addedPaths);
    }

    [Theory]
    [InlineData(LibraryMode.ByType, false)]
    [InlineData(LibraryMode.ByRemoteLibrary, false)]
    [InlineData(LibraryMode.ByType, true)]
    public async Task ThePluginLibrariesOfAnotherServerGetNoTypeFolderOfTheNewServer(LibraryMode mode, bool adopted)
    {
        // §10 row 443: the libraries by type, by remote library and adopted are the plugin's, so only the admin's gets it.
        MigratedToRemoteLibraries();
        var (filmsOfA, filmsOfC) = (Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var a = Imported(_serverA, (filmsOfA, "movies"));
        a.LibraryMode = mode;
        Imported(_serverC, (filmsOfC, "movies"));
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, LocalFilms, TypeFolderOf(_serverA, filmsOfA, TypeFolder.Movies)));
        _libraries.Add(Library("Di A", CollectionTypeOptions.movies, TypeFolderOf(_serverA, filmsOfA, TypeFolder.Movies)));
        a.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig
        {
            Kind = ServerLibraryKind.Movies,
            Name = "Di A",
            ItemId = IdOf("Di A"),
            RemoteLibraryId = mode == LibraryMode.ByRemoteLibrary ? filmsOfA : string.Empty,
            Adopted = adopted
        });
        foreach (var library in _libraries)
        {
            a.FollowedLibraryIds.Add(library.ItemId!);
        }

        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([("Film", TypeFolderOf(_serverC, filmsOfC, TypeFolder.Movies))], _addedPaths);
        Assert.Equal([TypeFolderOf(_serverA, filmsOfA, TypeFolder.Movies)], Locations("Di A"));
        Assert.Contains(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
    }

    [Fact]
    public async Task ASharedLibraryGetsNoTypeFolderOfTheNewServerFromPlacement()
    {
        // §10 row 443: the shared library is the plugin's; it takes the new server through its own check (row 442).
        MigratedToRemoteLibraries();
        var (filmsOfA, filmsOfC) = (Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        Imported(_serverC, (filmsOfC, "movies"));
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, LocalFilms, TypeFolderOf(_serverA, filmsOfA, TypeFolder.Movies)));
        _libraries.Add(Library("Tutti i server - Film", CollectionTypeOptions.movies, TypeFolderOf(_serverA, filmsOfA, TypeFolder.Movies)));
        var shared = _store.Configuration.SharedLibraries;
        shared.Enabled = true;
        shared.Libraries.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Tutti i server - Film", ItemId = IdOf("Tutti i server - Film") });
        foreach (var library in _libraries)
        {
            shared.FollowedLibraryIds.Add(library.ItemId!);
        }

        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([("Film", TypeFolderOf(_serverC, filmsOfC, TypeFolder.Movies))], _addedPaths);
        Assert.Equal([TypeFolderOf(_serverA, filmsOfA, TypeFolder.Movies)], Locations("Tutti i server - Film"));
    }

    [Fact]
    public async Task TheAutomaticLibrariesOfTheOtherServersGetNoFolderOfTheNewServer()
    {
        // D24: an automatic library holds only its own server, so the plugin leaves it so; D21: the switch makes the
        // new server's own library, and the page warns of the different sets.
        Migrated();
        _store.Configuration.Servers.Add(Automatic(_serverA, "A - Film", "a-film-id"));
        _store.Configuration.Servers.Add(Automatic(_serverB, "B - Film", string.Empty));
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        var renamed = Library("A - Film renamed", CollectionTypeOptions.movies, ServerFilms(_serverA));
        renamed.ItemId = "a-film-id";
        _libraries.Add(renamed);
        _libraries.Add(Library("B - Film", CollectionTypeOptions.movies, ServerFilms(_serverB)));
        _libraries.Add(Library("C - Film", CollectionTypeOptions.movies, ServerFilms(_serverC)));
        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([("Film", ServerFilms(_serverC))], _addedPaths);
    }

    [Fact]
    public async Task APlacedServerIsNotPlacedAgainAndAFolderTheAdminRemovedStaysOut()
    {
        Migrated();
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        _configured.Add(_serverC);
        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);
        await migration.Running;
        var film = _libraries.Single(library => library.Name == "Film");
        film.Locations = film.Locations.Where(location => location != ServerFilms(_serverC)).ToArray();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverB)], Locations("Film"));
        Assert.Equal(1, _scansQueued);
        _materializer.Verify(materializer => materializer.PrepareServerFolders(), Times.Once);
    }

    [Fact]
    public async Task WithNothingToAddTheServerIsPlacedWithoutAScan()
    {
        Migrated();
        _libraries.Add(Library("Locale", CollectionTypeOptions.movies, LocalFilms));
        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Empty(_addedPaths);
        Assert.Equal(0, _scansQueued);
        Assert.Contains(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
    }

    [Fact]
    public async Task AFailedAdditionLeavesTheServerForTheNextHookWithoutStoppingTheMigration()
    {
        Migrated();
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        _libraries.Add(Library("Altri film", CollectionTypeOptions.movies, ServerFilms(_serverA)));
        _failingAdds.Add("Altri film");
        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.DoesNotContain(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
        Assert.Equal(AutomaticMigrationError.None, _store.Configuration.LayoutMigration.Error);
        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
        Assert.Contains(ServerFilms(_serverC), Locations("Film"));
        Assert.Equal(1, _scansQueued);
        Assert.Null(migration.GetStatus(Root));

        _failingAdds.Clear();
        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverC)], Locations("Altri film"));
        Assert.Contains(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
        Assert.Equal(2, _scansQueued);
    }

    [Fact]
    public async Task AThrowingPlacementIsLoggedAndSetsNoError()
    {
        Migrated();
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA)));
        _materializer.Setup(materializer => materializer.PrepareServerFolders()).Throws(new IOException("/secret/path"));
        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal(AutomaticMigrationError.None, _store.Configuration.LayoutMigration.Error);
        Assert.DoesNotContain(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
        Assert.DoesNotContain(_logger.Lines, line => line.Contains("/secret/path", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InManualModeANewServerIsLeftToTheAdmin()
    {
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        _configured.Add(_serverC);
        foreach (var mode in new[] { LayoutMigrationMode.None, LayoutMigrationMode.Manual })
        {
            _store.Configuration.LayoutMigration.Mode = mode;
            var migration = await MigrationAsync();

            await migration.RunAsync(CancellationToken.None);
            await migration.Running;

            Assert.Equal(LayoutMigrationMode.Manual, migration.NewServerMode);
            Assert.Empty(_addedPaths);
        }
    }

    [Fact]
    public async Task TheMigrationRecordsTheServersItPlaced()
    {
        _store.Configuration.LayoutMigration.Mode = LayoutMigrationMode.None;
        OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));
        var migration = await MigrationAsync();

        await migration.Choose(LayoutMigrationMode.Automatic).Job;

        Assert.Equal(new[] { _serverA, _serverB }.Order(), _store.Configuration.LayoutMigration.PlacedServers.Order());
        Assert.Equal(LayoutMigrationMode.Automatic, migration.NewServerMode);
        Assert.Equal(2, _scansQueued);
    }

    [Fact]
    public async Task AServerThatLeftTheConfigurationLeavesThePlacedOnes()
    {
        Migrated();
        _store.Configuration.LayoutMigration.PlacedServers.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.DoesNotContain(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
        Assert.Equal(2, _store.Configuration.LayoutMigration.PlacedServers.Count);
    }

    [Fact]
    public async Task TheSettingsTurnANewInstallToAutomaticWithTheServersThereAlreadyPlaced()
    {
        // D23: a new install starts manual; the servers there when the settings choose automatic are the admin's. Its
        // titles are in the remote library layout, so the new server joins with its type folders (§10 row 443).
        _store.Configuration.LayoutMigration.Mode = LayoutMigrationMode.None;
        var (films, filmsOfC) = (Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        Imported(_serverC, (filmsOfC, "movies"));
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, TypeFolderOf(_serverA, films, TypeFolder.Movies)));
        var migration = await MigrationAsync();
        Assert.Equal(LayoutMigrationMode.Manual, migration.NewServerMode);

        var choice = migration.SetMode(LayoutMigrationMode.Automatic);
        await choice.Job;
        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal(LayoutMigrationChoiceOutcome.Chosen, choice.Outcome);
        Assert.Equal(LayoutMigrationMode.Automatic, migration.NewServerMode);
        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
        Assert.Equal([TypeFolderOf(_serverA, films, TypeFolder.Movies)], Locations("Film"));
        Assert.Equal(0, _scansQueued);
        Assert.Null(migration.GetStatus(Root));

        _configured.Add(_serverC);
        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([TypeFolderOf(_serverA, films, TypeFolder.Movies), TypeFolderOf(_serverC, filmsOfC, TypeFolder.Movies)], Locations("Film"));
    }

    [Fact]
    public async Task TheSettingsGoBackToManualOnceTheAutomaticRunIsDone()
    {
        Migrated();
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        var migration = await MigrationAsync();

        var choice = migration.SetMode(LayoutMigrationMode.Manual);
        _configured.Add(_serverC);
        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal(LayoutMigrationChoiceOutcome.Chosen, choice.Outcome);
        Assert.Equal(LayoutMigrationMode.Manual, _store.Configuration.LayoutMigration.Mode);
        Assert.Equal(LayoutMigrationMode.Manual, migration.NewServerMode);
        Assert.Empty(_addedPaths);

        // And to automatic again: the server linked meanwhile is the admin's.
        Assert.Equal(LayoutMigrationChoiceOutcome.Chosen, migration.SetMode(LayoutMigrationMode.Automatic).Outcome);
        await migration.RunAsync(CancellationToken.None);
        await migration.Running;
        Assert.Empty(_addedPaths);
        Assert.Contains(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
    }

    [Fact]
    public async Task TheSettingsCannotGoBackToManualWhileTheAutomaticRunIsUnfinished()
    {
        OldFilm("Dune", (_serverA, "a1"));
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));
        _store.Configuration.LayoutMigration.Mode = LayoutMigrationMode.Automatic;
        _store.Configuration.LayoutMigration.Step = AutomaticMigrationStep.AddPaths;
        _store.Configuration.LayoutMigration.Error = AutomaticMigrationError.AddFailed;
        var migration = await MigrationAsync();

        var manual = migration.SetMode(LayoutMigrationMode.Manual);
        var automatic = migration.SetMode(LayoutMigrationMode.Automatic);

        Assert.Equal(LayoutMigrationChoiceOutcome.NotAllowed, manual.Outcome);
        Assert.Equal(LayoutMigrationChoiceOutcome.Chosen, automatic.Outcome);
        Assert.Equal(AutomaticMigrationError.AddFailed, _store.Configuration.LayoutMigration.Error);
        Assert.Equal(LayoutMigrationMode.Automatic, _store.Configuration.LayoutMigration.Mode);
        Assert.Empty(_addedPaths);
    }

    [Fact]
    public async Task WhileTheBannerAsksTheSettingsMakeTheSameChoice()
    {
        _store.Configuration.LayoutMigration.Mode = LayoutMigrationMode.None;
        OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));
        var migration = await MigrationAsync();

        var choice = migration.SetMode(LayoutMigrationMode.Automatic);
        await choice.Job;

        Assert.Equal(LayoutMigrationChoiceOutcome.Started, choice.Outcome);
        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverB)], Locations("Film"));
        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
    }

    [Fact]
    public async Task TheSettingsRefuseAnUndefinedModeAndKeepManualOnANewInstall()
    {
        _store.Configuration.LayoutMigration.Mode = LayoutMigrationMode.None;
        var migration = await MigrationAsync();

        Assert.Equal(LayoutMigrationChoiceOutcome.NotAllowed, migration.SetMode(LayoutMigrationMode.None).Outcome);
        Assert.Equal(LayoutMigrationChoiceOutcome.NotAllowed, migration.SetMode((LayoutMigrationMode)3).Outcome);
        Assert.Equal(LayoutMigrationChoiceOutcome.Chosen, migration.SetMode(LayoutMigrationMode.Manual).Outcome);
        Assert.Equal(LayoutMigrationMode.Manual, migration.NewServerMode);
        Assert.Empty(_store.Configuration.LayoutMigration.PlacedServers);
    }

    [Fact]
    public async Task ALibraryCreatedBeforeItsRecordIsSavedGetsNoFolderAndTheServerIsNotPlaced()
    {
        // The manager made "A - Film" and has not saved its record yet: placement waits rather than take it for the admin's.
        Migrated();
        _store.Configuration.Servers.Add(new RemoteServerConfig { Id = _serverA, AutomaticLibraries = true });
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        _libraries.Add(Library("A - Film", CollectionTypeOptions.movies, ServerFilms(_serverA)));
        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Empty(_addedPaths);
        Assert.Equal(0, _scansQueued);
        Assert.DoesNotContain(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
    }

    [Fact]
    public async Task OnceTheRecordsAreSavedTheNextHookPlacesTheServerInTheSharedLibrariesOnly()
    {
        Migrated();
        var a = new RemoteServerConfig { Id = _serverA, AutomaticLibraries = true };
        _store.Configuration.Servers.Add(a);
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        _libraries.Add(Library("Serie", CollectionTypeOptions.tvshows, ServerSeries(_serverA), ServerSeries(_serverB)));
        _libraries.Add(Library("A - Film", CollectionTypeOptions.movies, ServerFilms(_serverA)));
        _configured.Add(_serverC);
        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);
        await migration.Running;
        _libraries.Add(Library("A - Serie", CollectionTypeOptions.tvshows, ServerSeries(_serverA)));
        a.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "A - Film" });
        a.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Series, Name = "A - Serie" });

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([("Film", ServerFilms(_serverC)), ("Serie", ServerSeries(_serverC))], _addedPaths);
        Assert.Equal([ServerFilms(_serverA)], Locations("A - Film"));
        Assert.Equal([ServerSeries(_serverA)], Locations("A - Serie"));
        Assert.Contains(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
        Assert.Equal(1, _scansQueued);
    }

    [Fact]
    public async Task WhileTheLibraryManagerIsBusyPlacementWaitsForTheNextHook()
    {
        Migrated();
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        _configured.Add(_serverC);
        _managerBusy = true;
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Empty(_addedPaths);
        Assert.DoesNotContain(_serverC, _store.Configuration.LayoutMigration.PlacedServers);

        _managerBusy = false;
        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverB), ServerFilms(_serverC)], Locations("Film"));
        Assert.Contains(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
    }

    [Fact]
    public async Task AJobThatStartsBeforeTheLibrariesAreReadMakesPlacementWait()
    {
        // The check follows the read: a library a job makes before the read is seen with the job still running.
        Migrated();
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        _library.Setup(library => library.GetVirtualFolders()).Returns(() =>
        {
            if (!_managerBusy)
            {
                _managerBusy = true;
                _libraries.Add(Library("A - Film", CollectionTypeOptions.movies, ServerFilms(_serverA)));
            }

            return _libraries.ToList();
        });
        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Empty(_addedPaths);
        Assert.DoesNotContain(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
    }

    [Fact]
    public async Task AnAutomaticLibraryRenamedInTheDashboardBeforeAFollowGetsNoFolderOfTheNewServer()
    {
        // Its record still has the old name and id: placement follows first, so the library stays automatic (§10 row 385).
        Migrated();
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        _libraries.Add(Library("A - Film", CollectionTypeOptions.movies, ServerFilms(_serverA)));
        _libraries.Add(Library("A - Serie", CollectionTypeOptions.tvshows, ServerSeries(_serverA)));
        var a = SwitchedOn(_serverA);
        Rename("A - Film", "Film di A");
        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([("Film", ServerFilms(_serverC))], _addedPaths);
        Assert.Equal([ServerFilms(_serverA)], Locations("Film di A"));
        Assert.Equal(["Film di A", "A - Serie"], a.AutomaticLibraryRegistry.Select(record => record.Name));
        Assert.Contains(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
    }

    [Fact]
    public async Task AnAutomaticLibraryRenamedByAResumeAtStartGetsNoFolderOfTheNewServer()
    {
        // The resume of a server rename left halfway runs no follow, so its record keeps the old name (§10 row 385).
        Migrated();
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        _libraries.Add(Library("A - Film", CollectionTypeOptions.movies, ServerFilms(_serverA)));
        _libraries.Add(Library("A - Serie", CollectionTypeOptions.tvshows, ServerSeries(_serverA)));
        var a = SwitchedOn(_serverA);
        _renamer.Setup(renamer => renamer.ResumeAsync(It.IsAny<CancellationToken>()))
            .Callback(() => Rename("A - Film", "Mare - Film"))
            .Returns(Task.CompletedTask);
        _configured.Add(_serverC);
        var migration = await MigrationAsync();
        await _manager!.ResumeRenamesAsync(CancellationToken.None);

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([("Film", ServerFilms(_serverC))], _addedPaths);
        Assert.Equal(["Mare - Film", "A - Serie"], a.AutomaticLibraryRegistry.Select(record => record.Name));
    }

    [Fact]
    public async Task AServerWithItsSwitchOnAndAKindLeftWithNoLibraryDoesNotHoldPlacementBack()
    {
        // A failed creation or a record the follow dropped is a stable state, so it does not stall placement (§10 row 384).
        Migrated();
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        _libraries.Add(Library("A - Film", CollectionTypeOptions.movies, ServerFilms(_serverA)));
        var a = SwitchedOn(_serverA);
        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([ServerLibraryKind.Movies], a.AutomaticLibraryRegistry.Select(record => record.Kind));
        Assert.Equal([("Film", ServerFilms(_serverC))], _addedPaths);
        Assert.Contains(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
    }

    [Fact]
    public async Task ALibraryWithNoIdMakesPlacementWaitWhileAServerHasAutomaticLibraries()
    {
        // It may be an automatic library renamed with no refresh, which the follow cannot match yet (decisions 213, 250).
        Migrated();
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        _libraries.Add(Library("A - Film", CollectionTypeOptions.movies, ServerFilms(_serverA)));
        _libraries.Add(Library("A - Serie", CollectionTypeOptions.tvshows, ServerSeries(_serverA)));
        SwitchedOn(_serverA);
        _libraries.Single(library => library.Name == "Film").ItemId = null;
        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Empty(_addedPaths);
        Assert.DoesNotContain(_serverC, _store.Configuration.LayoutMigration.PlacedServers);

        _libraries.Single(library => library.Name == "Film").ItemId = IdOf("Film");
        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([("Film", ServerFilms(_serverC))], _addedPaths);
    }

    [Fact]
    public async Task TheSettingsCannotGoBackToManualWhilePlacementRuns()
    {
        Migrated();
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        _configured.Add(_serverC);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scans.Setup(scans => scans.WaitIdleAsync(It.IsAny<CancellationToken>())).Returns(idle.Task);
        var migration = await MigrationAsync();
        await migration.RunAsync(CancellationToken.None);

        var choice = migration.SetMode(LayoutMigrationMode.Manual);
        idle.SetResult();
        await migration.Running;

        Assert.Equal(LayoutMigrationChoiceOutcome.Busy, choice.Outcome);
        Assert.Equal(LayoutMigrationMode.Automatic, _store.Configuration.LayoutMigration.Mode);
    }

    private static RemoteServerConfig Automatic(Guid server, string name, string itemId)
    {
        var config = new RemoteServerConfig { Id = server, AutomaticLibraries = true };
        config.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = name, ItemId = itemId });
        config.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Series, Name = name + " serie" });
        return config;
    }

    // A server with the switch on whose records are its libraries there now, all of them seen by the last follow.
    [Fact]
    public async Task ALibraryThereAtSwitchOnThatPassesAsTheOwnLibraryOfAServerGetsTheNewServer()
    {
        // The admin's library on the server folder, with no automatic one: it is the admin's (§10 rows 383, 393).
        Migrated();
        _libraries.Add(Library("Film di A", CollectionTypeOptions.movies, ServerFilms(_serverA)));
        var a = new RemoteServerConfig { Id = _serverA, AutomaticLibraries = true };
        a.PreexistingLibraryIds.Add(IdOf("Film di A"));
        _store.Configuration.Servers.Add(a);
        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([("Film di A", ServerFilms(_serverC))], _addedPaths);
        Assert.Contains(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
    }

    [Fact]
    public async Task ALibraryARemovedServerLeftRenamedInTheDashboardGetsNoFolderOfTheNewServer()
    {
        // The server came back with the same id, so its folder is a configured one again (§10 row 395).
        Migrated();
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        _libraries.Add(Library("A - Film", CollectionTypeOptions.movies, ServerFilms(_serverA)));
        var held = new RemovedServerLibrariesConfig { ServerId = _serverA };
        held.Libraries.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "A - Film", ItemId = IdOf("A - Film") });
        _store.Configuration.RemovedServerLibraries.Add(held);
        Rename("A - Film", "Vecchio A");
        _configured.Add(_serverC);
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([("Film", ServerFilms(_serverC))], _addedPaths);
        Assert.Equal([ServerFilms(_serverA)], Locations("Vecchio A"));
        Assert.Contains(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
    }

    private RemoteServerConfig SwitchedOn(Guid server)
    {
        var config = new RemoteServerConfig { Id = server, AutomaticLibraries = true };
        foreach (var library in _libraries)
        {
            config.FollowedLibraryIds.Add(library.ItemId!);
            var kind = library.Locations is [var only] && only == ServerFilms(server) ? ServerLibraryKind.Movies
                : library.Locations is [var series] && series == ServerSeries(server) ? ServerLibraryKind.Series
                : (ServerLibraryKind?)null;
            if (kind is { } found)
            {
                config.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = found, Name = library.Name, ItemId = library.ItemId! });
            }
        }

        _store.Configuration.Servers.Add(config);
        return config;
    }

    [Fact]
    public async Task ANewServerStaysToPlaceUntilTheScanHeldBackForASharedLibraryRuns()
    {
        Migrated();
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB)));
        _configured.Add(_serverC);
        var held = 2;
        var manager = new Mock<IServerLibraryManager>();
        manager.Setup(libraries => libraries.FollowForPlacement()).Returns(() => new PlacementLibraries(_libraries.ToList(), new HashSet<VirtualFolderInfo>()));
        manager.Setup(libraries => libraries.ScanAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => held-- <= 0);
        _managerOverride = manager.Object;
        var migration = await MigrationAsync();

        foreach (var _ in Enumerable.Range(0, 2))
        {
            await migration.RunAsync(CancellationToken.None);
            await migration.Running;
            Assert.DoesNotContain(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
        }

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Contains(_serverC, _store.Configuration.LayoutMigration.PlacedServers);
        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverB), ServerFilms(_serverC)], Locations("Film"));
        manager.Verify(libraries => libraries.ScanAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // A rename from the Dashboard, which gives the library a new id too.
    private void Rename(string name, string newName)
    {
        var library = _libraries.Single(library => library.Name == name);
        library.Name = newName;
        library.ItemId = IdOf(newName);
    }

    // A server whose remote libraries were read, each given as its id and remote type, all imported, in "none" mode.
    private RemoteServerConfig Imported(Guid server, params (string Id, string Type)[] libraries)
    {
        var config = new RemoteServerConfig { Id = server };
        foreach (var (id, type) in libraries)
        {
            config.RemoteLibraries.Add(new RemoteLibraryConfig { Id = id, Name = "Libreria " + id[..4], CollectionType = type });
            config.ImportedLibraryIds.Add(id);
        }

        _store.Configuration.Servers.Add(config);
        return config;
    }

    private string TypeFolderOf(Guid server, string library, TypeFolder type) => FederatedFolderLayout.TypeFolderPath(Root, server, library, type);

    // An automatic migration done with servers A and B, whose titles are in the remote library layout.
    private void MigratedToRemoteLibraries()
    {
        var migration = _store.Configuration.LayoutMigration;
        migration.Mode = LayoutMigrationMode.Automatic;
        migration.Step = AutomaticMigrationStep.Done;
        migration.PlacedServers.Add(_serverA);
        migration.PlacedServers.Add(_serverB);
    }

    // An automatic migration done with servers A and B.
    private void Migrated()
    {
        // A title of A in its 0.5 server folder keeps the stubs in the older layout.
        NewFilm(_serverA, "a0");
        var migration = _store.Configuration.LayoutMigration;
        migration.Mode = LayoutMigrationMode.Automatic;
        migration.Step = AutomaticMigrationStep.Done;
        migration.PlacedServers.Add(_serverA);
        migration.PlacedServers.Add(_serverB);
    }
}
