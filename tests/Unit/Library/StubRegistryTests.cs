using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Tests.Storage;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class StubRegistryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jellymesh-tests-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryConfigurationStore _store = new();
    private readonly CapturingLogger<StubRegistry> _logger = new();
    private readonly TestDatabase _database;

    public StubRegistryTests()
    {
        _store.Configuration.FederatedFolderPath = "/federata";
        _database = new TestDatabase(_folder);
    }

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private StubRegistry Create() => new(_database, _store, _logger);

    private static StubEntry Entry(string id, string path) => new()
    {
        Id = id,
        Kind = StubKind.Movie,
        RelativePath = path,
        IdentityKey = "key",
        Sources = [new StubSource(Guid.NewGuid(), "item", "source")]
    };

    private static async Task SeedAsync(StubRegistry registry, StubState state)
    {
        await registry.EnterAsync(CancellationToken.None);
        try
        {
            await registry.SaveAsync(state, CancellationToken.None);
        }
        finally
        {
            registry.Exit();
        }
    }

    [Fact]
    public async Task TheOldPathOfARenamedStubFindsItUntilTheRenameIsDone()
    {
        using var registry = Create();
        await SeedAsync(registry, new StubState
        {
            Stubs = [Entry("abc", "S/Film/A/A - Mare.strm")],
            Renames = [new StubRename("abc", "S/Film/A/A - Casa.strm")]
        });

        Assert.Equal("abc", registry.FindByPath("/federata/S/Film/A/A - Casa.strm")?.Id);
        Assert.Equal("abc", registry.FindByPath("/federata/S/Film/A/A - Mare.strm")?.Id);

        using var reloaded = Create();
        Assert.Equal("abc", reloaded.FindByPath("/federata/S/Film/A/A - Casa.strm")?.Id);

        await SeedAsync(registry, registry.State with { Renames = [] });
        Assert.Null(registry.FindByPath("/federata/S/Film/A/A - Casa.strm"));
    }

    // Every new item of every library is looked up here, so the lookup must not fail while the settings cannot be read.
    [Fact]
    public async Task WhileTheSettingsCannotBeReadNoPathFindsAStub()
    {
        using var registry = Create();
        await SeedAsync(registry, new StubState { Stubs = [Entry("abc", "S/Film/A/A - Mare.strm")] });
        _store.Unavailable = true;

        Assert.Null(registry.FindByPath("/federata/S/Film/A/A - Mare.strm"));
    }

    [Fact]
    public async Task ACurrentPathWinsOverTheOldPathOfAnotherStub()
    {
        using var registry = Create();
        await SeedAsync(registry, new StubState
        {
            Stubs = [Entry("abc", "S/Film/A/A - Mare.strm"), Entry("def", "S/Film/A/A - Casa.strm")],
            Renames = [new StubRename("abc", "S/Film/A/A - Casa.strm"), new StubRename("gone", "S/Film/B/B - Casa.strm")]
        });

        Assert.Equal("def", registry.FindByPath("/federata/S/Film/A/A - Casa.strm")?.Id);
        Assert.Null(registry.FindByPath("/federata/S/Film/B/B - Casa.strm"));
    }

    [Fact]
    public async Task ARemovedServerStaysMarkedAcrossARestart()
    {
        var server = Guid.NewGuid();
        using (var registry = Create())
        {
            Assert.True(await registry.RemoveServerAsync(server, () => Task.FromResult(true), CancellationToken.None));
        }

        using var restarted = Create();
        Assert.Equal([server], restarted.State.RemovedServers);
    }

    [Fact]
    public async Task ARemovalThatFailsTakesItsMarkBack()
    {
        var server = Guid.NewGuid();
        using (var registry = Create())
        {
            await Assert.ThrowsAsync<IOException>(() => registry.RemoveServerAsync(server, () => throw new IOException("disk full"), CancellationToken.None));
            Assert.Empty(registry.State.RemovedServers);
        }

        using var restarted = Create();
        Assert.Empty(restarted.State.RemovedServers);
    }

    [Fact]
    public async Task ARemovalThatFindsNoServerTakesItsMarkBack()
    {
        using var registry = Create();

        Assert.False(await registry.RemoveServerAsync(Guid.NewGuid(), () => Task.FromResult(false), CancellationToken.None));

        Assert.Empty(registry.State.RemovedServers);
    }

    [Fact]
    public async Task AFailedRemovalKeepsAMarkThatWasThereBefore()
    {
        var server = Guid.NewGuid();
        using var registry = Create();
        await registry.RemoveServerAsync(server, () => Task.FromResult(true), CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => registry.RemoveServerAsync(server, () => throw new IOException("disk full"), CancellationToken.None));

        Assert.Equal([server], registry.State.RemovedServers);
    }

    [Fact]
    public async Task ForgettingMarksEveryServerMissingFromTheSettings()
    {
        var configured = Guid.NewGuid();
        var inStubs = Guid.NewGuid();
        var inSeries = Guid.NewGuid();
        var inIndex = Guid.NewGuid();
        var markedBefore = Guid.NewGuid();
        using (var registry = Create())
        {
            await SeedAsync(registry, StubState.Empty with
            {
                Stubs = [Entry("a", "A.strm") with { Sources = [new StubSource(configured, "1", "1"), new StubSource(inStubs, "2", "2")] }],
                Series = [new SeriesStub { Id = "s", RelativePath = "S", WorkKey = "w", Sources = [new StubSource(inSeries, "3", "3")] }],
                RemovedServers = [markedBefore]
            });

            var forgotten = await registry.ForgetServersAsync(() => new HashSet<Guid> { configured }, [inIndex, configured, markedBefore], CancellationToken.None);

            Assert.Equal(3, forgotten);
        }

        using var restarted = Create();
        Assert.Equal(new HashSet<Guid> { markedBefore, inStubs, inSeries, inIndex }, restarted.State.RemovedServers.ToHashSet());
    }

    [Fact]
    public async Task ForgettingWithNoServerMissingMarksNothing()
    {
        var configured = Guid.NewGuid();
        using var registry = Create();
        await SeedAsync(registry, StubState.Empty with { Stubs = [Entry("a", "A.strm") with { Sources = [new StubSource(configured, "1", "1")] }] });

        Assert.Equal(0, await registry.ForgetServersAsync(() => new HashSet<Guid> { configured }, [configured], CancellationToken.None));

        Assert.Empty(registry.State.RemovedServers);
    }

    [Fact]
    public async Task ForgettingWaitsForTheLock()
    {
        using var registry = Create();
        await registry.EnterAsync(CancellationToken.None);

        var forgetting = registry.ForgetServersAsync(() => new HashSet<Guid>(), [Guid.NewGuid()], CancellationToken.None);
        await Task.Delay(50);
        var waited = !forgetting.IsCompleted;
        registry.Exit();

        Assert.True(waited);
        Assert.Equal(1, await forgetting);
    }

    [Fact]
    public async Task ForgettingReadsTheSettingsOnlyUnderTheLock()
    {
        var restored = Guid.NewGuid();
        var configured = new HashSet<Guid>();
        using var registry = Create();
        await registry.EnterAsync(CancellationToken.None);

        var forgetting = registry.ForgetServersAsync(() => configured, [restored], CancellationToken.None);
        await Task.Delay(50);
        configured.Add(restored);
        registry.Exit();

        Assert.Equal(0, await forgetting);
        Assert.Empty(registry.State.RemovedServers);
    }

    [Fact]
    public async Task TheRemovalRunsWithTheMarkSavedAndUnderTheLock()
    {
        var server = Guid.NewGuid();
        using var registry = Create();
        var markedDuringRemoval = false;
        var lockFree = true;

        await registry.RemoveServerAsync(
            server,
            async () =>
            {
                markedDuringRemoval = registry.State.RemovedServers.Contains(server);
                using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                try
                {
                    await registry.EnterAsync(wait.Token);
                    registry.Exit();
                }
                catch (OperationCanceledException)
                {
                    lockFree = false;
                }

                return true;
            },
            CancellationToken.None);

        Assert.True(markedDuringRemoval);
        Assert.False(lockFree);
    }

    [Fact]
    public async Task AServerHasTitlesWhileOneOfItsStubsOrSeriesIsLeft()
    {
        var (withStub, withSeries, without) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using var registry = Create();
        await SeedAsync(registry, new StubState
        {
            Stubs = [Entry("abc", "S/Film/A/A.strm") with { ServerId = withStub }],
            Series = [new SeriesStub { Id = "s", ServerId = withSeries, RelativePath = "S/Serie/B", WorkKey = "series|b", Sources = [] }]
        });

        Assert.True(registry.HasTitlesOf(withStub));
        Assert.True(registry.HasTitlesOf(withSeries));
        Assert.False(registry.HasTitlesOf(without));
    }

    // A removed server's libraries wait for its titles (decision 278), so a registry that cannot tell says it has some.
    [Fact]
    public async Task ARegistryNotReadOrReopenedCannotTellThatAServerHasNoTitles()
    {
        var server = Guid.NewGuid();
        _database.Suspended = true;
        using var registry = Create();
        Assert.True(registry.HasTitlesOf(server));

        _database.Suspended = false;
        await registry.EnterAsync(CancellationToken.None);
        registry.Exit();
        Assert.False(registry.HasTitlesOf(server));

        _database.Reopen();
        Assert.True(registry.HasTitlesOf(server));
    }

    [Fact]
    public async Task AfterRowsThatDoNotReadOnlyAFullMaterializationTellsThatAServerHasNoTitles()
    {
        var server = Guid.NewGuid();
        using (var registry = Create())
        {
            await SeedAsync(registry, FullState());
        }

        await _database.WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE stub_entries SET kind = 'Unknown' WHERE id = 'abc';";
            command.ExecuteNonQuery();
        }, CancellationToken.None);

        using var damaged = Create();
        Assert.True(damaged.HasTitlesOf(server));

        await SeedAsync(damaged, damaged.State with { RemovedServers = [server] });
        Assert.True(damaged.HasTitlesOf(server));

        await damaged.EnterAsync(CancellationToken.None);
        try
        {
            await damaged.SaveMaterializedAsync(damaged.State, CancellationToken.None);
        }
        finally
        {
            damaged.Exit();
        }

        Assert.False(damaged.HasTitlesOf(server));
    }

    // The sync tells whether anyone saved the registry since its last run by this number (Q386).
    [Fact]
    public async Task EachSaveRaisesTheGenerationAndAFailedOneDoesNot()
    {
        using var registry = Create();
        var start = registry.Generation;

        await registry.SaveAsync(new StubState { Stubs = [Entry("abc", "Film/A/A.strm")] }, CancellationToken.None);
        await registry.SaveAsync(registry.State, CancellationToken.None);
        Assert.Equal(start + 2, registry.Generation);

        _database.InterruptNextWrite();
        await Assert.ThrowsAnyAsync<IOException>(() => registry.SaveAsync(new StubState(), CancellationToken.None));
        Assert.Equal(start + 2, registry.Generation);
    }

    [Fact]
    public async Task AStubIsFoundByIdAndByPath()
    {
        using var registry = Create();
        await registry.SaveAsync(new StubState { Stubs = [Entry("abc", "Film/A/A.strm")] }, CancellationToken.None);

        Assert.Equal("Film/A/A.strm", registry.Find("abc")?.RelativePath);
        Assert.Equal("abc", registry.FindByPath("/federata/Film/A/A.strm")?.Id);
        Assert.Null(registry.Find("other"));
        Assert.Null(registry.FindByPath("/media/film/A.mkv"));
        Assert.Null(registry.FindByPath(null));
    }

    [Fact]
    public async Task TheStateSurvivesARestart()
    {
        var source = new StubSource(Guid.NewGuid(), "item", "version") { LibraryId = "library" };
        using (var registry = Create())
        {
            await registry.SaveAsync(new StubState { Stubs = [Entry("abc", "Film/A/A.strm") with { Sources = [source] }] }, CancellationToken.None);
        }

        using var reloaded = Create();

        Assert.Equal(source, Assert.Single(reloaded.Find("abc")!.Sources));
    }

    [Fact]
    public async Task NoFolderMeansNoStubs()
    {
        _store.Configuration.FederatedFolderPath = string.Empty;
        using var registry = Create();
        await registry.SaveAsync(new StubState { Stubs = [Entry("abc", "Film/A/A.strm")] }, CancellationToken.None);

        Assert.Null(registry.FindByPath("/federata/Film/A/A.strm"));
    }

    [Fact]
    public async Task TheServerOfAStubAndTheVersionGroupsSurviveARestart()
    {
        var server = Guid.NewGuid();
        var local = Guid.NewGuid();
        var group = new VersionGroup { Id = "g", IdentityKey = "key", StubIds = ["abc", "def"], LocalItemIds = [local], PrimaryStubId = "abc" };
        using (var registry = Create())
        {
            await registry.SaveAsync(
                new StubState
                {
                    Stubs = [Entry("abc", "x/Film/A/A - Casa.strm") with { ServerId = server }, Entry("def", "y/Film/A/A - Ufficio.strm")],
                    Series = [new SeriesStub { Id = "s", RelativePath = "x/Serie/S", WorkKey = "w", ServerId = server }],
                    Groups = [group],
                    Labels = new Dictionary<Guid, string> { [server] = "Casa" }
                },
                CancellationToken.None);
        }

        using var reloaded = Create();

        Assert.Equal("Casa", Assert.Single(reloaded.State.Labels, label => label.Key == server).Value);
        Assert.Equal(server, reloaded.Find("abc")!.ServerId);
        Assert.Equal(server, Assert.Single(reloaded.State.Series).ServerId);
        var loaded = Assert.Single(reloaded.State.Groups);
        Assert.Equal(("g", "key", "abc"), (loaded.Id, loaded.IdentityKey, loaded.PrimaryStubId));
        Assert.Equal(["abc", "def"], loaded.StubIds);
        Assert.Equal([local], loaded.LocalItemIds);
    }

    [Fact]
    public async Task AVersionGroupIsFoundByEachOfItsStubsAndLocalCopies()
    {
        var local = Guid.NewGuid();
        var group = new VersionGroup { Id = "g", IdentityKey = "key", StubIds = ["abc", "def"], LocalItemIds = [local] };
        using var registry = Create();
        await SeedAsync(registry, new StubState { Stubs = [Entry("abc", "x/Film/A/A.strm"), Entry("def", "y/Film/A/A.strm")], Groups = [group] });

        Assert.Equal("g", registry.FindGroup("abc")?.Id);
        Assert.Equal("g", registry.FindGroup("def")?.Id);
        Assert.Equal("g", registry.FindGroupOfLocal(local)?.Id);
        Assert.Null(registry.FindGroup("other"));
        Assert.Null(registry.FindGroupOfLocal(Guid.NewGuid()));
    }

    [Fact]
    public async Task AStubListedInTwoGroupsDoesNotBreakTheRegistry()
    {
        var first = new VersionGroup { Id = "g1", IdentityKey = "key", StubIds = ["abc"] };
        var second = new VersionGroup { Id = "g2", IdentityKey = "key", StubIds = ["abc"] };
        using var registry = Create();
        await SeedAsync(registry, new StubState { Stubs = [Entry("abc", "x/Film/A/A.strm")], Groups = [first, second] });

        Assert.Equal("g1", registry.FindGroup("abc")?.Id);
    }

    // Every part of the state, with something in each list.
    private static StubState FullState()
    {
        var server = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var other = Guid.Parse("66666666-7777-8888-9999-000000000000");
        var source = new StubSource(server, "item", "version") { LibraryId = "library" };
        return new StubState
        {
            Stubs =
            [
                Entry("abc", "x/Film/A/A.strm") with
                {
                    ServerId = server,
                    Sources = [source, new StubSource(other, "other", "other version")],
                    ContentHash = "content",
                    ImageTag = "image",
                    TechnicalTag = "technical",
                    UrlTag = "url",
                    StrmWrittenUtc = new DateTime(2026, 11, 18, 10, 30, 15, 123, DateTimeKind.Utc).AddTicks(4567)
                },
                Entry("def", "x/Serie/S/Season 01/S - S01E01-E02.strm") with
                {
                    Kind = StubKind.Episode,
                    Sources = [source],
                    SeriesStubId = "s",
                    SeasonNumber = 1,
                    EpisodeNumber = 1,
                    EpisodeNumberEnd = 2
                }
            ],
            Series =
            [
                new SeriesStub
                {
                    Id = "s",
                    ServerId = server,
                    RelativePath = "x/Serie/S",
                    WorkKey = "work",
                    Sources = [source],
                    ContentHash = "series content",
                    ImageTag = "series image",
                    NeedsRefresh = true,
                    Seasons = [new SeasonStub { RelativePath = "x/Serie/S/Season 01", ContentHash = "season", ImageTag = "season image" }, new SeasonStub { RelativePath = "x/Serie/S/Specials" }]
                }
            ],
            Groups = [new VersionGroup { Id = "g", IdentityKey = "key", StubIds = ["abc", "def"], LocalItemIds = [other], PrimaryStubId = "abc" }],
            Labels = new Dictionary<Guid, string> { [server] = "Casa", [other] = "Ufficio" },
            RemovedServers = [other],
            ServerNames = new Dictionary<Guid, string> { [server] = "Server A" },
            Renames = [new StubRename("abc", "x/Film/A/Old.strm") { RemovedItemId = other, Forgets = true }, new StubRename("def", "x/Serie/S/Old.strm")]
        };
    }

    private static string Json(StubState state) => JsonSerializer.Serialize(state);

    [Fact]
    public async Task EveryPartOfTheStateComesBackWholeAfterARestart()
    {
        using (var registry = Create())
        {
            await SeedAsync(registry, FullState());
        }

        using var restarted = Create();

        Assert.Equal(Json(FullState()), Json(restarted.State));
        Assert.Equal(FullState().Stubs[0].StrmWrittenUtc, restarted.Find("abc")!.StrmWrittenUtc);
    }

    [Fact]
    public async Task ChangesKeepTheOrderAndDropWhatIsGoneAcrossARestart()
    {
        using var registry = Create();
        await SeedAsync(registry, FullState());
        var state = registry.State;
        var changed = state with
        {
            Stubs = [Entry("new", "x/Film/N/N.strm"), state.Stubs[1] with { ContentHash = "changed" }],
            Series = [],
            Groups = [],
            Labels = new Dictionary<Guid, string>(),
            RemovedServers = [],
            ServerNames = new Dictionary<Guid, string>(),
            Renames = []
        };

        await SeedAsync(registry, changed);

        using var restarted = Create();
        Assert.Equal(Json(changed), Json(restarted.State));
        Assert.Null(restarted.Find("abc"));
    }

    [Fact]
    public async Task ASaveOfTheSameStateWritesNothingButStillCounts()
    {
        using var registry = Create();
        await SeedAsync(registry, FullState());
        var writes = _database.Writes;
        var generation = registry.Generation;

        await SeedAsync(registry, FullState());
        await SeedAsync(registry, registry.State with { });

        Assert.Equal(writes, _database.Writes);
        Assert.Equal(generation + 2, registry.Generation);
    }

    [Fact]
    public async Task ASaveInterruptedHalfwayLeavesThePreviousState()
    {
        using var registry = Create();
        await SeedAsync(registry, FullState());
        _database.InterruptNextWrite();

        await Assert.ThrowsAsync<IOException>(() => SeedAsync(registry, new StubState { Stubs = [Entry("new", "x/Film/N/N.strm")] }));

        Assert.Equal(Json(FullState()), Json(registry.State));
        using var restarted = Create();
        Assert.Equal(Json(FullState()), Json(restarted.State));

        // The next save starts from what the tables still hold.
        await SeedAsync(registry, registry.State with { Renames = [] });
        using var again = Create();
        Assert.Equal(Json(FullState() with { Renames = [] }), Json(again.State));
    }

    [Fact]
    public async Task ASuspendedStoreGivesAnEmptyRegistryThatSavesNothingUntilItIsReadAgain()
    {
        using (var registry = Create())
        {
            await SeedAsync(registry, FullState());
        }

        _database.Suspended = true;
        using var suspended = Create();
        Assert.Empty(suspended.State.Stubs);
        Assert.Null(suspended.Find("abc"));
        Assert.Contains(_logger.Lines, line => line.Contains("stub registry", StringComparison.Ordinal));
        await Assert.ThrowsAsync<IOException>(() => suspended.SaveAsync(new StubState(), CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => suspended.EnterAsync(CancellationToken.None));
        var generation = suspended.Generation;

        _database.Suspended = false;
        await suspended.EnterAsync(CancellationToken.None);
        suspended.Exit();

        Assert.Equal(Json(FullState()), Json(suspended.State));
        Assert.Equal(generation + 1, suspended.Generation);
        await SeedAsync(suspended, suspended.State with { Renames = [] });
        using var restarted = Create();
        Assert.Equal("abc", restarted.Find("abc")?.Id);
    }

    [Fact]
    public async Task AReopenedDatabaseIsReadAgainAtTheNextLock()
    {
        using var registry = Create();
        await SeedAsync(registry, new StubState { Stubs = [Entry("x", "x/Film/X/X.strm"), Entry("y", "x/Film/Y/Y.strm")] });

        // The copy the database came back from does not hold the last stub.
        await _database.WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM stub_entries WHERE id = 'y';";
            command.ExecuteNonQuery();
        }, CancellationToken.None);
        _database.Reopen();

        await registry.EnterAsync(CancellationToken.None);
        StubState saved;
        try
        {
            saved = registry.State with { Stubs = [.. registry.State.Stubs, Entry("z", "x/Film/Z/Z.strm")] };
            await registry.SaveAsync(saved, CancellationToken.None);
        }
        finally
        {
            registry.Exit();
        }

        using var restarted = Create();
        Assert.Equal(saved.Stubs.Select(stub => stub.Id), restarted.State.Stubs.Select(stub => stub.Id));
        Assert.Equal(["x", "z"], restarted.State.Stubs.Select(stub => stub.Id));
    }

    public static TheoryData<Exception> ConversionFailures() => new()
    {
        new InvalidOperationException("null value"),
        new OverflowException("too large"),
    };

    [Theory]
    [MemberData(nameof(ConversionFailures))]
    public async Task ARowThatDoesNotConvertGivesAnEmptyRegistry(Exception failure)
    {
        using (var registry = Create())
        {
            await SeedAsync(registry, FullState());
        }

        _database.FailNextRead(failure);
        using var damaged = Create();

        Assert.Empty(damaged.State.Stubs);
        Assert.Contains(_logger.Lines, line => line.Contains("damaged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RowsThatDoNotReadGiveAnEmptyRegistryWrittenWholeAtTheNextSave()
    {
        using (var registry = Create())
        {
            await SeedAsync(registry, FullState());
        }

        await _database.WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE stub_entries SET kind = 'Unknown' WHERE id = 'abc';";
            command.ExecuteNonQuery();
        }, CancellationToken.None);

        using var damaged = Create();
        Assert.Empty(damaged.State.Stubs);
        Assert.Contains(_logger.Lines, line => line.Contains("damaged", StringComparison.Ordinal));

        await SeedAsync(damaged, new StubState { Stubs = [Entry("new", "x/Film/N/N.strm")] });
        using var restarted = Create();
        Assert.Equal("new", Assert.Single(restarted.State.Stubs).Id);
        Assert.Empty(restarted.State.Series);
    }
}
