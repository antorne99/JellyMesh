using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class StubRegistryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jellymesh-tests-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryConfigurationStore _store = new();
    private readonly CapturingLogger<StubRegistry> _logger = new();

    public StubRegistryTests() => _store.Configuration.FederatedFolderPath = "/federata";

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private StubRegistry Create() => new(_folder, _store, _logger);

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
    public void ARegistryWrittenBeforeTheLastEpisodeNumberExistedStillLoads()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(
            Path.Combine(_folder, StubRegistry.FileName),
            "{\"Version\":1,\"Stubs\":[{\"Id\":\"abc\",\"Kind\":\"Episode\",\"RelativePath\":\"Serie/S/S.strm\",\"IdentityKey\":\"key\",\"EpisodeNumber\":1,\"Sources\":[]}],\"Series\":[]}");

        using var registry = Create();

        Assert.Equal(1, registry.Find("abc")?.EpisodeNumber);
        Assert.Null(registry.Find("abc")?.EpisodeNumberEnd);
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
    public void ADamagedFileGivesAnEmptyRegistryAndALogLine()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, StubRegistry.FileName), "{ not json");

        using var registry = Create();

        Assert.Empty(registry.State.Stubs);
        Assert.Contains(_logger.Lines, line => line.Contains("stub registry", StringComparison.Ordinal));
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
    public void ARegistryOfTheFlatLayoutLoadsAsTheCurrentVersionWithNoServerAndNoGroup()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(
            Path.Combine(_folder, StubRegistry.FileName),
            "{\"Version\":1,\"Stubs\":[{\"Id\":\"abc\",\"Kind\":\"Movie\",\"RelativePath\":\"Film/A/A.strm\",\"IdentityKey\":\"key\",\"Sources\":[]}],\"Series\":[{\"Id\":\"s\",\"RelativePath\":\"Serie/S\",\"WorkKey\":\"w\"}]}");

        using var registry = Create();

        Assert.Equal(2, registry.State.Version);
        Assert.Null(registry.Find("abc")!.ServerId);
        Assert.Null(Assert.Single(registry.State.Series).ServerId);
        Assert.Empty(registry.State.Groups);
    }

    [Fact]
    public void ARegistryOfAnUnknownVersionIsNotRead()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, StubRegistry.FileName), "{\"Version\":99,\"Stubs\":[],\"Series\":[]}");

        using var registry = Create();

        Assert.Empty(registry.State.Stubs);
        Assert.Contains(_logger.Lines, line => line.Contains("unsupported format", StringComparison.Ordinal));
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
}
