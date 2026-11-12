using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class ServerStubRenamerTests : IDisposable
{
    private static readonly Guid _server = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000001");
    private static readonly Guid _other = Guid.Parse("5e6f7a8b-0000-4000-8000-000000000002");

    private readonly string _data = Path.Combine(Path.GetTempPath(), "jellymesh-rename-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryConfigurationStore _store = new();
    private readonly StubRegistry _registry;
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IWatchStateCarrier> _carrier = new();
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly List<IReadOnlyList<WatchStateMove>> _carries = [];
    private readonly List<IReadOnlyCollection<Guid>> _forgets = [];
    private readonly ServerStubRenamer _renamer;
    private Func<WatchStateMove, WatchStateMoveStatus> _status = _ => WatchStateMoveStatus.Carried;
    private bool _deletesFail;

    public ServerStubRenamerTests()
    {
        _store.Configuration.FederatedFolderPath = Path.Combine(_data, "federata");
        _registry = new StubRegistry(Path.Combine(_data, "plugin"), _store, new CapturingLogger<StubRegistry>());
        _library.Setup(library => library.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns((string key, Type type) => IdOf(key, type));
        _library.Setup(library => library.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
        _carrier.Setup(carrier => carrier.CarryAsync(It.IsAny<IReadOnlyList<WatchStateMove>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<WatchStateMove> moves, CancellationToken _) =>
            {
                _carries.Add(moves);
                var outcomes = moves.Select(move => new WatchStateMoveOutcome(move, _status(move), [])).ToList();
                return new WatchStateCarryReport(outcomes)
                {
                    ForgettableOldItemIds = outcomes.Where(outcome => outcome.Status == WatchStateMoveStatus.Carried).Select(outcome => outcome.Move.FromItemId).ToList()
                };
            });
        _carrier.Setup(carrier => carrier.ForgetRemovedAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
            {
                _forgets.Add(ids);
                return ids.ToList();
            });
        _renamer = new ServerStubRenamer(_registry, _store, _library.Object, _carrier.Object, new CapturingLogger<ServerStubRenamer>(), Delete);
    }

    private string Root => _store.Configuration.FederatedFolderPath;

    private static string FilmPath(string label, Guid? server = null) => Path.Combine((server ?? _server).ToString("D"), "Film", "A (2020)", "A (2020) - " + label + ".strm");

    private static string EpisodePath(string label) => Path.Combine(_server.ToString("D"), "Serie", "S", "Season 01", "S S01E01 - " + label + ".strm");

    public void Dispose()
    {
        _registry.Dispose();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, true);
        }
    }

    [Fact]
    public async Task TheStubsOfTheServerAreWrittenUnderTheNewLabelNextToTheOldOnes()
    {
        await SeedAsync(Film("f", FilmPath("Casa")), Episode("e", EpisodePath("Casa")), Film("o", FilmPath("Altro", _other)));
        Write(FilmPath("Casa"), "url-f");
        Write(Path.Combine(Path.GetDirectoryName(FilmPath("Casa"))!, "movie.nfo"), "nfo");
        Write(EpisodePath("Casa"), "url-e");
        Write(Path.ChangeExtension(EpisodePath("Casa"), ".nfo"), "nfo-e");
        Write(Path.ChangeExtension(EpisodePath("Casa"), null) + "-thumb.jpg", "thumb");

        Assert.Equal(2, await _renamer.RelabelAsync(_server, "Mare", CancellationToken.None));

        Assert.Equal("url-f", Read(FilmPath("Mare")));
        Assert.Equal("url-e", Read(EpisodePath("Mare")));
        Assert.Equal("nfo-e", Read(Path.ChangeExtension(EpisodePath("Mare"), ".nfo")));
        Assert.Equal("thumb", Read(Path.ChangeExtension(EpisodePath("Mare"), null) + "-thumb.jpg"));
        Assert.True(File.Exists(Full(FilmPath("Casa"))));
        Assert.True(File.Exists(Full(EpisodePath("Casa"))));

        var state = _registry.State;
        Assert.Equal([FilmPath("Mare"), EpisodePath("Mare"), FilmPath("Altro", _other)], state.Stubs.Select(stub => stub.RelativePath));
        Assert.All(state.Stubs.Take(2), stub => Assert.Equal((null, null, null), (stub.UrlTag, stub.TechnicalTag, (DateTime?)stub.StrmWrittenUtc)));
        Assert.Equal("tag", state.Stubs[2].UrlTag);
        Assert.Equal([new StubRename("f", FilmPath("Casa")), new StubRename("e", EpisodePath("Casa"))], state.Renames);
        Assert.Equal("Mare", state.Labels[_server]);
        Assert.Equal("Altro", state.Labels[_other]);
        Assert.True(_renamer.IsPending(_server));
        Assert.False(_renamer.IsPending(_other));
    }

    [Fact]
    public async Task ANewNameWithTheSameLabelMovesNothing()
    {
        await SeedAsync(Film("f", FilmPath("Casa")));
        Write(FilmPath("Casa"), "url");

        Assert.Equal(0, await _renamer.RelabelAsync(_server, "casa", CancellationToken.None));
        Assert.Equal(0, await _renamer.RelabelAsync(_server, "Casa: trailer", CancellationToken.None));

        Assert.Equal(FilmPath("Casa"), _registry.State.Stubs.Single().RelativePath);
        Assert.Empty(_registry.State.Renames);
    }

    [Fact]
    public async Task ALabelTakenByAnotherServerGetsTheGuidDigits()
    {
        await SeedAsync(Film("f", FilmPath("Casa")));

        await _renamer.RelabelAsync(_server, "Altro", CancellationToken.None);

        Assert.Equal("Altro 1a2b3c4d", _registry.State.Labels[_server]);
        Assert.Equal(FilmPath("Altro 1a2b3c4d"), _registry.State.Stubs.Single().RelativePath);
    }

    [Fact]
    public async Task AStubNotOnDiskMovesWithNothingToWaitFor()
    {
        await SeedAsync(Film("f", FilmPath("Casa")));

        Assert.Equal(1, await _renamer.RelabelAsync(_server, "Mare", CancellationToken.None));

        Assert.Equal(FilmPath("Mare"), _registry.State.Stubs.Single().RelativePath);
        Assert.Empty(_registry.State.Renames);
        Assert.False(File.Exists(Full(FilmPath("Mare"))));
    }

    [Fact]
    public async Task OnceTheNewItemIsScannedTheStateIsCarriedAndTheOldStrmGoes()
    {
        await RenamedFilmAsync();
        var old = Scanned(FilmPath("Casa"), new Movie());
        var renamed = Scanned(FilmPath("Mare"), new Movie { OwnerId = old.Id });

        await _renamer.FinishAsync(CancellationToken.None);

        var move = _carries.Single().Single();
        Assert.Equal((old.Id, "f", FilmPath("Casa"), true), (move.FromItemId, move.FromStubId, move.FromPath, move.RemovesOldItem));
        Assert.Equal([new WatchStateTarget(renamed.Id, _server)], move.Targets);
        Assert.False(File.Exists(Full(FilmPath("Casa"))));
        Assert.True(File.Exists(Full(FilmPath("Mare"))));
        Assert.True(File.Exists(Full(Path.Combine(Path.GetDirectoryName(FilmPath("Casa"))!, "movie.nfo"))));
        Assert.Equal([new StubRename("f", FilmPath("Casa")) { RemovedItemId = old.Id, Forgets = true }], _registry.State.Renames);
        Assert.True(_renamer.IsPending(_server));
    }

    [Fact]
    public async Task TheOldItemGoneAfterTheNextScanIsForgottenAndTheRenameIsDone()
    {
        await RenamedFilmAsync();
        var old = Scanned(FilmPath("Casa"), new Movie());
        Scanned(FilmPath("Mare"), new Movie());
        await _renamer.FinishAsync(CancellationToken.None);
        await _renamer.FinishAsync(CancellationToken.None);
        Assert.Empty(_forgets);

        _items.Remove(old.Id);
        await _renamer.FinishAsync(CancellationToken.None);

        Assert.Equal([old.Id], _forgets.Single());
        Assert.Single(_carries);
        Assert.Empty(_registry.State.Renames);
        Assert.False(_renamer.IsPending(_server));
        Assert.Null(_registry.FindByPath(Full(FilmPath("Casa"))));
    }

    [Fact]
    public async Task AnEpisodeLosesItsOldStrmNfoAndThumbnail()
    {
        await SeedAsync(Episode("e", EpisodePath("Casa")));
        Write(EpisodePath("Casa"), "url");
        Write(Path.ChangeExtension(EpisodePath("Casa"), ".nfo"), "nfo");
        Write(Path.ChangeExtension(EpisodePath("Casa"), null) + "-thumb.jpg", "thumb");
        await _renamer.RelabelAsync(_server, "Mare", CancellationToken.None);
        Scanned(EpisodePath("Casa"), new Episode());
        Scanned(EpisodePath("Mare"), new Episode());

        await _renamer.FinishAsync(CancellationToken.None);

        var folder = Path.GetDirectoryName(Full(EpisodePath("Casa")))!;
        Assert.Equal(
            ["S S01E01 - Mare-thumb.jpg", "S S01E01 - Mare.nfo", "S S01E01 - Mare.strm"],
            Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ANewItemNotScannedYetWaits()
    {
        await RenamedFilmAsync();
        Scanned(FilmPath("Casa"), new Movie());

        await _renamer.FinishAsync(CancellationToken.None);

        Assert.Empty(_carries);
        Assert.True(File.Exists(Full(FilmPath("Casa"))));
        Assert.Equal([new StubRename("f", FilmPath("Casa"))], _registry.State.Renames);
    }

    [Fact]
    public async Task AnOldItemNeverScannedHasNothingToCarry()
    {
        await RenamedFilmAsync();
        Scanned(FilmPath("Mare"), new Movie());

        await _renamer.FinishAsync(CancellationToken.None);

        Assert.Empty(_carries);
        Assert.False(File.Exists(Full(FilmPath("Casa"))));
        Assert.Empty(_registry.State.Renames);
    }

    [Fact]
    public async Task WithNeitherItemInALibrary_TheOldFileStaysForTheScanThatBringsItBack()
    {
        await RenamedFilmAsync();

        await _renamer.FinishAsync(CancellationToken.None);

        Assert.Empty(_carries);
        Assert.True(File.Exists(Full(FilmPath("Casa"))));
        Assert.Equal([new StubRename("f", FilmPath("Casa"))], _registry.State.Renames);
    }

    [Theory]
    [InlineData(WatchStateMoveStatus.NotLinked)]
    [InlineData(WatchStateMoveStatus.NotFound)]
    [InlineData(WatchStateMoveStatus.Failed)]
    public async Task ACarryNotDoneKeepsTheOldFileForTheNextScan(WatchStateMoveStatus status)
    {
        await RenamedFilmAsync();
        Scanned(FilmPath("Casa"), new Movie());
        Scanned(FilmPath("Mare"), new Movie());
        _status = _ => status;

        await _renamer.FinishAsync(CancellationToken.None);

        Assert.True(File.Exists(Full(FilmPath("Casa"))));
        Assert.Equal([new StubRename("f", FilmPath("Casa"))], _registry.State.Renames);
    }

    [Fact]
    public async Task AStubThatLeftTheRegistryLosesItsOldFiles()
    {
        await RenamedFilmAsync();
        Scanned(FilmPath("Casa"), new Movie());
        await _registry.EnterAsync(CancellationToken.None);
        await _registry.SaveAsync(_registry.State with { Stubs = [] }, CancellationToken.None);
        _registry.Exit();

        await _renamer.FinishAsync(CancellationToken.None);

        Assert.Empty(_carries);
        Assert.False(File.Exists(Full(FilmPath("Casa"))));
        Assert.Empty(_registry.State.Renames);
    }

    [Fact]
    public async Task AStubThatLeftTheRegistryHoldsUpNoRename()
    {
        await RenamedFilmAsync();
        await SaveAsync(_registry.State with { Stubs = [Film("o", FilmPath("Altro", _other))] });

        Assert.False(_renamer.IsPending(_server));
        Assert.False(_renamer.IsPending(_other));
    }

    [Fact]
    public async Task ARenameBackOverTheOldFileOfAStubThatLeftKeepsTheNewFile()
    {
        await RenamedFilmAsync();
        await SaveAsync(_registry.State with { Stubs = [Film("g", FilmPath("Mare"))] });
        Write(FilmPath("Mare"), "url-g");

        await _renamer.RelabelAsync(_server, "Casa", CancellationToken.None);
        await _renamer.FinishAsync(CancellationToken.None);

        Assert.Equal("url-g", Read(FilmPath("Casa")));
        Assert.Equal([new StubRename("g", FilmPath("Mare"))], _registry.State.Renames);
    }

    [Fact]
    public async Task ARenameBackAfterTheOldFilesOfAStubThatLeftCouldNotGoKeepsTheNewFile()
    {
        await RenamedFilmAsync();
        await SaveAsync(_registry.State with { Stubs = [Film("g", FilmPath("Mare"))] });
        Write(FilmPath("Mare"), "url-g");
        _deletesFail = true;

        await _renamer.RelabelAsync(_server, "Casa", CancellationToken.None);
        _deletesFail = false;
        await _renamer.FinishAsync(CancellationToken.None);

        Assert.Equal("url-g", Read(FilmPath("Casa")));
        Assert.Equal([new StubRename("g", FilmPath("Mare"))], _registry.State.Renames);
    }

    [Fact]
    public async Task ARenameBackWithNoFolderOverTheOldFileOfAStubThatLeftKeepsTheFileWrittenLater()
    {
        await RenamedFilmAsync();
        await SaveAsync(_registry.State with { Stubs = [Film("g", FilmPath("Mare"))] });
        var root = Root;
        _store.Configuration.FederatedFolderPath = string.Empty;

        await _renamer.RelabelAsync(_server, "Casa", CancellationToken.None);
        _store.Configuration.FederatedFolderPath = root;
        Write(FilmPath("Casa"), "url-g");
        await _renamer.FinishAsync(CancellationToken.None);

        Assert.Equal(FilmPath("Casa"), _registry.State.Stubs.Single().RelativePath);
        Assert.Equal("url-g", Read(FilmPath("Casa")));
        Assert.Empty(_registry.State.Renames);
    }

    [Fact]
    public async Task ARenameBackOverTheRemovedItemOfAStubThatLeftForgetsNothing()
    {
        await RenamedFilmAsync();
        var old = Scanned(FilmPath("Casa"), new Movie());
        Scanned(FilmPath("Mare"), new Movie());
        await _renamer.FinishAsync(CancellationToken.None);
        await SaveAsync(_registry.State with { Stubs = [Film("g", FilmPath("Mare"))] });
        Write(FilmPath("Mare"), "url-g");

        await _renamer.RelabelAsync(_server, "Casa", CancellationToken.None);
        Assert.DoesNotContain(_registry.State.Renames, rename => rename.StubId == "f");

        _items.Remove(old.Id);
        await _renamer.FinishAsync(CancellationToken.None);

        Assert.Empty(_forgets);
        Assert.Equal("url-g", Read(FilmPath("Casa")));
    }

    [Fact]
    public async Task AnEntryOfAStubThatLeftAtThePathOfALiveStubIsDroppedWithItsFilesKept()
    {
        await SeedAsync(Film("g", FilmPath("Casa")));
        Write(FilmPath("Casa"), "url-g");
        await SaveAsync(_registry.State with { Renames = [new StubRename("f", FilmPath("Casa"))] });

        await _renamer.FinishAsync(CancellationToken.None);

        Assert.Equal("url-g", Read(FilmPath("Casa")));
        Assert.Empty(_registry.State.Renames);
    }

    [Fact]
    public async Task AnEntryOfAStubThatLeftAtThePathOfALiveStubForgetsNotItsRemovedItem()
    {
        await SeedAsync(Film("g", FilmPath("Casa")));
        Write(FilmPath("Casa"), "url-g");
        var removed = IdOf(Full(FilmPath("Casa")), typeof(Movie));
        await SaveAsync(_registry.State with { Renames = [new StubRename("f", FilmPath("Casa")) { RemovedItemId = removed, Forgets = true }] });

        await _renamer.FinishAsync(CancellationToken.None);

        Assert.Empty(_forgets);
        Assert.Equal("url-g", Read(FilmPath("Casa")));
        Assert.Empty(_registry.State.Renames);
    }

    private async Task SaveAsync(StubState state)
    {
        await _registry.EnterAsync(CancellationToken.None);
        try
        {
            await _registry.SaveAsync(state, CancellationToken.None);
        }
        finally
        {
            _registry.Exit();
        }
    }

    private static Guid IdOf(string key, Type type) => new(MD5.HashData(Encoding.UTF8.GetBytes(type.FullName + key)));

    private static StubEntry Film(string id, string path) => new()
    {
        Id = id,
        ServerId = Guid.Parse(path[..36]),
        Kind = StubKind.Movie,
        RelativePath = path,
        IdentityKey = id,
        Sources = [new StubSource(Guid.Parse(path[..36]), "item-" + id, "source")],
        UrlTag = "tag",
        TechnicalTag = "tech",
        StrmWrittenUtc = DateTime.UnixEpoch
    };

    private static StubEntry Episode(string id, string path) => Film(id, path) with { Kind = StubKind.Episode, SeasonNumber = 1, EpisodeNumber = 1 };

    private async Task RenamedFilmAsync()
    {
        await SeedAsync(Film("f", FilmPath("Casa")));
        Write(FilmPath("Casa"), "url");
        Write(Path.Combine(Path.GetDirectoryName(FilmPath("Casa"))!, "movie.nfo"), "nfo");
        await _renamer.RelabelAsync(_server, "Mare", CancellationToken.None);
    }

    private T Scanned<T>(string relative, T item)
        where T : Video
    {
        item.Path = Full(relative);
        item.Id = IdOf(item.Path, item is Episode ? typeof(Episode) : typeof(Movie));
        _items[item.Id] = item;
        return item;
    }

    private async Task SeedAsync(params StubEntry[] stubs)
    {
        await _registry.EnterAsync(CancellationToken.None);
        try
        {
            var labels = new Dictionary<Guid, string> { [_server] = "Casa", [_other] = "Altro" };
            await _registry.SaveAsync(StubState.Empty with { Stubs = stubs, Labels = labels }, CancellationToken.None);
        }
        finally
        {
            _registry.Exit();
        }
    }

    private string Full(string relative) => FederatedPaths.Resolve(Root, relative);

    private void Write(string relative, string text)
    {
        var path = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Read(string relative) => File.ReadAllText(Full(relative));

    private void Delete(string path)
    {
        if (_deletesFail)
        {
            throw new IOException("busy");
        }

        File.Delete(path);
    }
}
