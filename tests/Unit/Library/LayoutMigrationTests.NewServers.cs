using System;
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
        // D23: a new install starts manual; the servers there when the settings choose automatic are the admin's.
        _store.Configuration.LayoutMigration.Mode = LayoutMigrationMode.None;
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA)));
        var migration = await MigrationAsync();
        Assert.Equal(LayoutMigrationMode.Manual, migration.NewServerMode);

        var choice = migration.SetMode(LayoutMigrationMode.Automatic);
        await choice.Job;
        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal(LayoutMigrationChoiceOutcome.Chosen, choice.Outcome);
        Assert.Equal(LayoutMigrationMode.Automatic, migration.NewServerMode);
        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
        Assert.Equal([ServerFilms(_serverA)], Locations("Film"));
        Assert.Equal(0, _scansQueued);
        Assert.Null(migration.GetStatus(Root));

        _configured.Add(_serverC);
        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverC)], Locations("Film"));
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

    private static RemoteServerConfig Automatic(Guid server, string name, string itemId)
    {
        var config = new RemoteServerConfig { Id = server, AutomaticLibraries = true };
        config.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = name, ItemId = itemId });
        return config;
    }

    // An automatic migration done with servers A and B.
    private void Migrated()
    {
        var migration = _store.Configuration.LayoutMigration;
        migration.Mode = LayoutMigrationMode.Automatic;
        migration.Step = AutomaticMigrationStep.Done;
        migration.PlacedServers.Add(_serverA);
        migration.PlacedServers.Add(_serverB);
    }
}
