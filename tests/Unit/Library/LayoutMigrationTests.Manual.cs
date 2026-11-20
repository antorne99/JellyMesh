using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// Migrazione delle cartelle, manual (spec F45, §10 rows 444-445): the admin moves the libraries by hand, with the page listing
// the paths to add and remove, and the plugin moves its own libraries once the watch state is carried.
public sealed partial class LayoutMigrationTests
{
    [Fact]
    public async Task AManualCarryMovesTheLibrariesThePluginKeepsToTheTypeFolders()
    {
        BothServersImport();
        KeepFilmsOfA();
        var old = ServerFolderFilm(_serverA, "a1");
        var dune = NewFilm(_serverA, "a1");
        _libraries.Add(Library("A - Film", CollectionTypeOptions.movies, ServerFilms(_serverA), NewFilms(_serverA)));
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB), NewFilms(_serverA), NewFilms(_serverB)));
        Row(_mario, old).IsFavorite = true;
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.True(Row(_mario, dune).IsFavorite);
        Assert.Equal([NewFilms(_serverA)], Locations("A - Film"));
        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverB), NewFilms(_serverA), NewFilms(_serverB)], Locations("Film"));
    }

    [Fact]
    public async Task TheHookOfAManualCarryDoesNotWaitForThePluginLibraries()
    {
        // The hook runs inside a scan, and moving the plugin libraries waits for one.
        BothServersImport();
        ServerFolderFilm(_serverA, "a1");
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, ServerFilms(_serverA), ServerFilms(_serverB), NewFilms(_serverA), NewFilms(_serverB)));
        var started = new TaskCompletionSource();
        var reconcile = new TaskCompletionSource();
        var manager = new Mock<IServerLibraryManager>();
        manager.Setup(libraries => libraries.ReconcileAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            started.TrySetResult();
            return reconcile.Task;
        });
        _managerOverride = manager.Object;
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain(_registry.State.Stubs, stub => FederatedPaths.RemoteLibraryOf(stub.RelativePath) is null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        reconcile.SetResult();
        await migration.Running;
    }

    private void KeepFilmsOfA()
    {
        var a = _store.Configuration.Servers.Single(server => server.Id == _serverA);
        a.LibraryMode = LibraryMode.ByType;
        a.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "A - Film", ItemId = IdOf("A - Film") });
    }
}
