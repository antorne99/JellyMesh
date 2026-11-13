using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The automatic migration (Q55-Q57): the plugin adds the server folders, scans, carries, removes the old paths and scans
// again, saving each step so that a restart or a retry resumes there.
public sealed partial class LayoutMigrationTests
{
    private readonly Mock<ILibraryScans> _scans = new();
    private readonly Mock<ILibraryMaterializer> _materializer = new();
    private readonly List<(string Library, string Path)> _addedPaths = [];
    private readonly List<(string Library, string Path)> _removedPaths = [];
    private readonly List<Guid> _forgotten = [];
    private readonly HashSet<string> _failingAdds = [];
    private readonly HashSet<string> _failingRemovals = [];
    private LayoutMigration? _migration;
    private int _scansQueued;

    private string LocalFilms => Path.Combine(_data, "media", "film");

    private string LocalSeries => Path.Combine(_data, "media", "serie");

    [Fact]
    public async Task TheStatusAsksForTheChoiceWhileNoneIsMade()
    {
        _store.Configuration.LayoutMigration.Mode = LayoutMigrationMode.None;
        OldFilm("Dune", (_serverA, "a1"));

        var status = (await MigrationAsync()).GetStatus(Root)!;

        Assert.Equal(LayoutMigrationMode.None, status.Mode);
        Assert.Null(status.AutomaticStep);
        Assert.False(status.Running);
    }

    [Fact]
    public async Task TheAutomaticMigrationAddsTheFoldersOfTheSameKindCarriesAndRemovesTheOldPaths()
    {
        _store.Configuration.LayoutMigration.Mode = LayoutMigrationMode.None;
        var (_, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var a = NewFilm(_serverA, "a1");
        var (oldShow, oldShowItem) = OldShow("Lost", (_serverA, "s1"));
        OldEpisode(oldShow, (_serverA, "e1"));
        var (newShow, newShowItem) = NewShow(_serverA, "s1");
        NewEpisode(newShow, _serverA, "e1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, LocalFilms, OldFilms));
        _libraries.Add(Library("Serie", CollectionTypeOptions.tvshows, LocalSeries, OldSeries));
        _libraries.Add(Library("Misto", CollectionTypeOptions.mixed, OldFilms, OldSeries));
        _libraries.Add(Library("Musica", CollectionTypeOptions.music, Path.Combine(_data, "media", "musica")));
        Row(_mario, oldItem).IsFavorite = true;
        Row(_luigi, oldShowItem).IsFavorite = true;
        var migration = await MigrationAsync();

        var choice = migration.Choose(LayoutMigrationMode.Automatic);
        await choice.Job;

        Assert.Equal(LayoutMigrationChoiceOutcome.Started, choice.Outcome);
        Assert.Equal([LocalFilms, ServerFilms(_serverA), ServerFilms(_serverB)], Locations("Film"));
        Assert.Equal([LocalSeries, ServerSeries(_serverA), ServerSeries(_serverB)], Locations("Serie"));
        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverB), ServerSeries(_serverA), ServerSeries(_serverB)], Locations("Misto"));
        Assert.Equal([Path.Combine(_data, "media", "musica")], Locations("Musica"));
        Assert.True(Row(_mario, a).IsFavorite);
        Assert.True(Row(_luigi, newShowItem).IsFavorite);
        Assert.DoesNotContain(_registry.State.Stubs, stub => stub.ServerId is null);
        Assert.DoesNotContain(_registry.State.Series, series => series.ServerId is null);
        Assert.Equal(2, _scansQueued);
        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
        Assert.Equal(AutomaticMigrationError.None, _store.Configuration.LayoutMigration.Error);
        Assert.Null(migration.GetStatus(Root));
        _materializer.Verify(materializer => materializer.PrepareServerFolders(), Times.Once);
    }

    [Fact]
    public async Task OnlyTheOldKindOfALibraryGetsServerFolders()
    {
        // Each library gets the server folders of the old kinds it holds, and no other kind (Q56).
        var (oldShow, _) = OldShow("Lost", (_serverA, "s1"));
        OldEpisode(oldShow, (_serverA, "e1"));
        var (newShow, _) = NewShow(_serverA, "s1");
        NewEpisode(newShow, _serverA, "e1");
        OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));
        _libraries.Add(Library("Serie", CollectionTypeOptions.tvshows, OldSeries));

        await (await MigrationAsync()).Choose(LayoutMigrationMode.Automatic).Job;

        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverB)], Locations("Film"));
        Assert.Equal([ServerSeries(_serverA), ServerSeries(_serverB)], Locations("Serie"));
    }

    [Fact]
    public async Task TheAutomaticCarryTellsTheCarrierTheOldItemGoesAndForgetsItOnceGone()
    {
        // The automatic migration removes the old item right after the carry, so it sets the signal (decisions 179, 187).
        var (_, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));
        var moves = new List<WatchStateMove>();
        _outcome = move =>
        {
            moves.Add(move);
            return new WatchStateMoveOutcome(move, WatchStateMoveStatus.Carried, []);
        };

        await (await MigrationAsync()).Choose(LayoutMigrationMode.Automatic).Job;

        Assert.True(Assert.Single(moves).RemovesOldItem);
        Assert.Equal([oldItem.Id], _forgotten);
        Assert.False(_items.ContainsKey(oldItem.Id));
    }

    [Fact]
    public async Task TheManualCarryLeavesTheSignalOff()
    {
        OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        AllPathsLinked();
        var moves = new List<WatchStateMove>();
        _outcome = move =>
        {
            moves.Add(move);
            return new WatchStateMoveOutcome(move, WatchStateMoveStatus.Carried, []);
        };

        await (await MigrationAsync()).RunAsync(CancellationToken.None);

        Assert.False(Assert.Single(moves).RemovesOldItem);
        Assert.Empty(_forgotten);
    }

    [Fact]
    public async Task AServerFolderAlreadyInTheLibraryIsNotAddedAgain()
    {
        OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms, ServerFilms(_serverA) + Path.DirectorySeparatorChar));

        await (await MigrationAsync()).Choose(LayoutMigrationMode.Automatic).Job;

        Assert.Equal([("Film", ServerFilms(_serverB))], _addedPaths);
    }

    [Fact]
    public async Task AFailedAddStopsThereWithTheOldPathsAndARetryResumes()
    {
        var (old, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var a = NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));
        _libraries.Add(Library("Altri film", CollectionTypeOptions.movies, OldFilms));
        Row(_mario, oldItem).IsFavorite = true;
        _failingAdds.Add("Altri film");
        var migration = await MigrationAsync();

        await migration.Choose(LayoutMigrationMode.Automatic).Job;

        var status = migration.GetStatus(Root)!;
        Assert.Equal(AutomaticMigrationError.AddFailed, status.Error);
        Assert.Equal("Altri film", status.ErrorLibrary);
        Assert.Equal(AutomaticMigrationStep.AddPaths, status.AutomaticStep);
        Assert.Equal(LayoutMigrationStep.AddPaths, status.Step);
        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverB)], Assert.Single(status.Libraries, library => library.Name == "Altri film").MissingPaths);
        Assert.Contains(OldFilms, Locations("Altri film"));
        Assert.NotNull(_registry.Find(old.Id));
        Assert.Equal(0, _scansQueued);

        // The hooks do not go past an error; the retry from the banner does.
        var added = _addedPaths.Count;
        await migration.RunAsync(CancellationToken.None);
        await migration.Running;
        Assert.Equal(0, _scansQueued);
        Assert.Equal(added, _addedPaths.Count);
        Assert.Empty(_removedPaths);
        Assert.NotNull(_registry.Find(old.Id));
        Assert.False(Row(_mario, a).IsFavorite);

        _failingAdds.Clear();
        var retry = migration.Choose(LayoutMigrationMode.Automatic);
        await retry.Job;

        Assert.Equal(LayoutMigrationChoiceOutcome.Started, retry.Outcome);
        Assert.True(Row(_mario, a).IsFavorite);
        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverB)], Locations("Altri film"));
        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
        Assert.Equal(AutomaticMigrationError.None, _store.Configuration.LayoutMigration.Error);
    }

    [Fact]
    public async Task AFailedRemovalStopsThereAndARetryRedoesNothingElse()
    {
        OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));
        _failingRemovals.Add("Film");
        var migration = await MigrationAsync();

        await migration.Choose(LayoutMigrationMode.Automatic).Job;

        var status = migration.GetStatus(Root)!;
        Assert.Equal(AutomaticMigrationError.RemoveFailed, status.Error);
        Assert.Equal("Film", status.ErrorLibrary);
        Assert.Equal(LayoutMigrationStep.RemoveOldPaths, status.Step);
        Assert.Equal([OldFilms], Assert.Single(status.Libraries).OldPaths);
        Assert.DoesNotContain(_registry.State.Stubs, stub => stub.ServerId is null);
        Assert.Equal(1, _scansQueued);
        var added = _addedPaths.Count;

        _failingRemovals.Clear();
        await migration.Choose(LayoutMigrationMode.Automatic).Job;

        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverB)], Locations("Film"));
        Assert.Equal(added, _addedPaths.Count);
        Assert.Equal(2, _scansQueued);
        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
    }

    [Fact]
    public async Task AStepThatThrowsStopsWithAnErrorAndARetryResumesFromIt()
    {
        OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));
        _scans.Setup(scans => scans.QueueAndWaitAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("/secret/path"));
        var migration = await MigrationAsync();

        await migration.Choose(LayoutMigrationMode.Automatic).Job;

        Assert.Equal(AutomaticMigrationError.Failed, _store.Configuration.LayoutMigration.Error);
        Assert.Equal(AutomaticMigrationStep.Scan, _store.Configuration.LayoutMigration.Step);
        Assert.DoesNotContain(_logger.Lines, line => line.Contains("/secret/path", StringComparison.Ordinal));

        SetUpScans();
        await migration.Choose(LayoutMigrationMode.Automatic).Job;

        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
        Assert.Equal(2, _addedPaths.Count);
    }

    [Fact]
    public async Task AfterARestartTheHooksResumeTheSavedStep()
    {
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms, ServerFilms(_serverA), ServerFilms(_serverB)));
        _store.Configuration.LayoutMigration.Mode = LayoutMigrationMode.Automatic;
        _store.Configuration.LayoutMigration.Step = AutomaticMigrationStep.RemoveOldPaths;
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.Empty(_addedPaths);
        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverB)], Locations("Film"));
        Assert.Equal(1, _scansQueued);
        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
    }

    [Fact]
    public async Task TitlesThatWaitKeepTheMigrationAtTheCarryUntilAHookFinishesIt()
    {
        var (_, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var a = NewFilm(_serverA, "a1");
        _items.Remove(a.Id);
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));
        Row(_mario, oldItem).IsFavorite = true;
        var migration = await MigrationAsync();

        await migration.Choose(LayoutMigrationMode.Automatic).Job;

        Assert.Equal(AutomaticMigrationStep.Carry, _store.Configuration.LayoutMigration.Step);
        Assert.Contains(OldFilms, Locations("Film"));
        Assert.True(_items.ContainsKey(oldItem.Id));

        // The scan brings the new item, and the hook after it carries and goes on.
        _items[a.Id] = a;
        await migration.RunAsync(CancellationToken.None);
        await migration.Running;

        Assert.True(Row(_mario, a).IsFavorite);
        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverB)], Locations("Film"));
        Assert.Equal(AutomaticMigrationStep.Done, _store.Configuration.LayoutMigration.Step);
    }

    [Fact]
    public async Task AServerFolderLeavingTheLibrariesBeforeTheCarryStopsTheMigration()
    {
        OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms, ServerFilms(_serverA)));
        _store.Configuration.LayoutMigration.Mode = LayoutMigrationMode.Automatic;
        _store.Configuration.LayoutMigration.Step = AutomaticMigrationStep.Carry;
        var migration = await MigrationAsync();

        await migration.RunAsync(CancellationToken.None);

        Assert.Equal(AutomaticMigrationError.NewPathsLeft, _store.Configuration.LayoutMigration.Error);
        Assert.Equal(AutomaticMigrationStep.AddPaths, _store.Configuration.LayoutMigration.Step);
        Assert.Single(_registry.State.Stubs, stub => stub.ServerId is null);
    }

    [Fact]
    public async Task ServerFoldersWithNoLibraryOnAnOldPathStopTheMigration()
    {
        // No library holds the old Film path, so the plugin has nowhere to add the server Film folders (Q215).
        var (old, _) = OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Serie", CollectionTypeOptions.tvshows, OldSeries));
        var migration = await MigrationAsync();

        await migration.Choose(LayoutMigrationMode.Automatic).Job;

        var status = migration.GetStatus(Root)!;
        Assert.Equal(AutomaticMigrationError.FoldersInNoLibrary, status.Error);
        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverB)], status.UnlinkedFolders);
        Assert.NotNull(_registry.Find(old.Id));
        Assert.Equal(0, _scansQueued);
        Assert.Single(_libraries);
    }

    [Fact]
    public async Task WithoutServersTheAutomaticMigrationWaits()
    {
        OldFilm("Dune", (_serverA, "a1"));
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));
        _configured.Clear();
        var migration = await MigrationAsync();

        await migration.Choose(LayoutMigrationMode.Automatic).Job;

        Assert.Empty(_addedPaths);
        Assert.Equal(0, _scansQueued);
        Assert.Equal(AutomaticMigrationStep.AddPaths, _store.Configuration.LayoutMigration.Step);
        Assert.Equal(AutomaticMigrationError.None, _store.Configuration.LayoutMigration.Error);
    }

    [Fact]
    public async Task ManualCanSwitchToAutomaticButNotBack()
    {
        OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms, ServerFilms(_serverA)));
        var migration = await MigrationAsync();

        var automatic = migration.Choose(LayoutMigrationMode.Automatic);
        await automatic.Job;
        var manual = migration.Choose(LayoutMigrationMode.Manual);

        Assert.Equal(LayoutMigrationChoiceOutcome.Started, automatic.Outcome);
        Assert.Equal(LayoutMigrationChoiceOutcome.NotAllowed, manual.Outcome);
        Assert.Equal(LayoutMigrationMode.Automatic, _store.Configuration.LayoutMigration.Mode);
        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverB)], Locations("Film"));
    }

    [Fact]
    public async Task AnOldPathAddedAfterTheEndIsShownAndANewRunRemovesIt()
    {
        OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));
        var migration = await MigrationAsync();
        await migration.Choose(LayoutMigrationMode.Automatic).Job;
        var film = _libraries.Single(library => library.Name == "Film");
        film.Locations = [.. film.Locations, OldFilms];

        var status = migration.GetStatus(Root)!;
        var again = migration.Choose(LayoutMigrationMode.Automatic);
        await again.Job;

        Assert.Equal(AutomaticMigrationStep.Done, status.AutomaticStep);
        Assert.False(status.Running);
        Assert.Equal(LayoutMigrationChoiceOutcome.Started, again.Outcome);
        Assert.Equal([ServerFilms(_serverA), ServerFilms(_serverB)], Locations("Film"));
        Assert.Null(migration.GetStatus(Root));
    }

    [Fact]
    public async Task ChoosingManualRunsTheManualMigrationAndChangesNoLibrary()
    {
        _store.Configuration.LayoutMigration.Mode = LayoutMigrationMode.None;
        var (_, oldItem) = OldFilm("Dune", (_serverA, "a1"));
        var a = NewFilm(_serverA, "a1");
        AllPathsLinked();
        Row(_mario, oldItem).IsFavorite = true;
        var migration = await MigrationAsync();

        var choice = migration.Choose(LayoutMigrationMode.Manual);
        await choice.Job;

        Assert.Equal(LayoutMigrationChoiceOutcome.Chosen, choice.Outcome);
        Assert.Equal(LayoutMigrationMode.Manual, _store.Configuration.LayoutMigration.Mode);
        Assert.True(Row(_mario, a).IsFavorite);
        Assert.Empty(_addedPaths);
        Assert.Empty(_removedPaths);
        Assert.Equal(0, _scansQueued);
    }

    [Fact]
    public async Task NoModeAndNothingToMigrateAreRefused()
    {
        _store.Configuration.LayoutMigration.Mode = LayoutMigrationMode.None;
        var migration = await MigrationAsync();

        Assert.Equal(LayoutMigrationChoiceOutcome.NotAllowed, migration.Choose(LayoutMigrationMode.None).Outcome);
        Assert.Equal(LayoutMigrationChoiceOutcome.NothingToMigrate, migration.Choose(LayoutMigrationMode.Automatic).Outcome);
        Assert.Equal(LayoutMigrationChoiceOutcome.NothingToMigrate, migration.Choose(LayoutMigrationMode.Manual).Outcome);
        Assert.Equal(LayoutMigrationMode.None, _store.Configuration.LayoutMigration.Mode);
    }

    [Fact]
    public async Task ASecondChoiceWhileTheMigrationRunsIsBusy()
    {
        OldFilm("Dune", (_serverA, "a1"));
        NewFilm(_serverA, "a1");
        _libraries.Add(Library("Film", CollectionTypeOptions.movies, OldFilms));
        var scan = new TaskCompletionSource();
        _scans.Setup(scans => scans.QueueAndWaitAsync(It.IsAny<CancellationToken>())).Returns(scan.Task);
        var migration = await MigrationAsync();

        var first = migration.Choose(LayoutMigrationMode.Automatic);
        await WaitForAsync(() => _store.Configuration.LayoutMigration.Step == AutomaticMigrationStep.Scan);
        var second = migration.Choose(LayoutMigrationMode.Automatic);
        var status = migration.GetStatus(Root)!;
        scan.SetResult();
        await first.Job;

        Assert.Equal(LayoutMigrationChoiceOutcome.Busy, second.Outcome);
        Assert.True(status.Running);
        Assert.Equal(AutomaticMigrationStep.Scan, status.AutomaticStep);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private string ServerFilms(Guid server) => FederatedFolderLayout.ServerMoviesPath(Root, server);

    private string ServerSeries(Guid server) => FederatedFolderLayout.ServerSeriesPath(Root, server);

    private string[] Locations(string library) => _libraries.Single(folder => folder.Name == library).Locations;

    // Jellyfin's calls as the F02 research saw them: an add appends the path, a removal drops only the path as stored.
    private void SetUpLibraryPaths()
    {
        _library.Setup(library => library.AddMediaPath(It.IsAny<string>(), It.IsAny<MediaPathInfo>()))
            .Callback((string name, MediaPathInfo path) =>
            {
                if (_failingAdds.Contains(name))
                {
                    throw new FileNotFoundException("missing");
                }

                _addedPaths.Add((name, path.Path));
                var folder = _libraries.Single(folder => folder.Name == name);
                folder.Locations = [.. folder.Locations, path.Path];
            });
        _library.Setup(library => library.RemoveMediaPath(It.IsAny<string>(), It.IsAny<string>()))
            .Callback((string name, string path) =>
            {
                if (_failingRemovals.Contains(name))
                {
                    return;
                }

                _removedPaths.Add((name, path));
                var folder = _libraries.Single(folder => folder.Name == name);
                folder.Locations = folder.Locations.Where(location => !string.Equals(location, path, StringComparison.Ordinal)).ToArray();
            });
        SetUpScans();
    }

    private void SetUpScans()
    {
        _scans.Setup(scans => scans.WaitIdleAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _scans.Setup(scans => scans.QueueAndWaitAsync(It.IsAny<CancellationToken>())).Callback(() => _scansQueued++).Returns(Task.CompletedTask);
    }
}
