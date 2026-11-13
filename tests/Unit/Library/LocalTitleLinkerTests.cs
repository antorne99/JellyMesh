using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

/// <summary>Q245: a new local title is linked to its stubs as soon as Jellyfin reads its metadata.</summary>
public sealed class LocalTitleLinkerTests : IDisposable
{
    private readonly StubFixture _fixture = new();
    private readonly Mock<IVersionLinker> _versions = new();
    private readonly CapturingLogger<LocalTitleLinker> _logger = new();

    public void Dispose() => _fixture.Dispose();

    private LocalTitleLinker Linker() => new(
        _fixture.Registry,
        _fixture.Resolver,
        _fixture.Sync.Index,
        _fixture.Sync.Registry,
        _fixture.Sync.Store,
        _fixture.Library.Object,
        _versions.Object,
        _logger);

    private Guid AddLocalFilm(string tmdb = "1")
    {
        var id = Guid.NewGuid();
        _fixture.LocalCatalog.Items.Add(new CatalogItem
        {
            ServerId = "local",
            ItemId = id.ToString("N"),
            Kind = ContentKind.Movie,
            Title = "Film",
            Year = 2020,
            Ids = new ContentIds(tmdb)
        });
        return id;
    }

    private void VerifyLinked(Guid[] local, Times times) =>
        _versions.Verify(linker => linker.LinkLocalAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(local)), It.IsAny<CancellationToken>()), times);

    private void VerifyNothingLinked() =>
        _versions.Verify(linker => linker.LinkLocalAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);

    private static VersionGroup Group(string id, string[] stubs, params Guid[] local) => new() { Id = id, IdentityKey = id, StubIds = stubs, LocalItemIds = local };

    [Fact]
    public async Task ARefreshedFilmJoinsItsStubAtOnce()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var files = _fixture.Files;
        var local = AddLocalFilm();

        await Linker().LinkAsync(local, CancellationToken.None);

        var group = Assert.Single(_fixture.Registry.State.Groups);
        Assert.Equal([local], group.LocalItemIds);
        Assert.Equal([_fixture.Registry.State.Stubs.Single().Id], group.StubIds);
        VerifyLinked([local], Times.Once());
        Assert.Equal(files, _fixture.Files);
    }

    [Fact]
    public async Task TheGroupOfTwoServersKeepsItsId()
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary("films-b", "Film B", "movies"));
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        _fixture.Sync.Client.Add("films-b", RemoteItemType.Movie, TestItems.Movie("mb"));
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.Sync.AddServerAsync("B", "films-b");
        await _fixture.SyncAndMaterializeAsync();
        var before = Assert.Single(_fixture.Registry.State.Groups);
        var local = AddLocalFilm();

        await Linker().LinkAsync(local, CancellationToken.None);

        var after = Assert.Single(_fixture.Registry.State.Groups);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.StubIds, after.StubIds);
        Assert.Equal([local], after.LocalItemIds);
    }

    [Fact]
    public async Task OtherTitlesWaitForTheNextSync()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("n", "Altro", 2021, "2"));
        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);
        var files = _fixture.Files;
        var local = AddLocalFilm();

        await Linker().LinkAsync(local, CancellationToken.None);

        Assert.Single(_fixture.Registry.State.Stubs);
        Assert.Equal(files, _fixture.Files);
        Assert.Equal([local], Assert.Single(_fixture.Registry.State.Groups).LocalItemIds);
    }

    [Fact]
    public async Task AFilmThatMatchesNoStubLinksNothing()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var local = AddLocalFilm("9");

        await Linker().LinkAsync(local, CancellationToken.None);

        Assert.Empty(_fixture.Registry.State.Groups);
        VerifyNothingLinked();
    }

    [Fact]
    public async Task ATitleWhoseGroupNeedsAStubNotWrittenYetWaitsForTheSync()
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary("films-b", "Film B", "movies"));
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        _fixture.Sync.Client.Add("films-b", RemoteItemType.Movie, TestItems.Movie("mb"));
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        await _fixture.Sync.AddServerAsync("B", "films-b");
        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);
        var local = AddLocalFilm();

        await Linker().LinkAsync(local, CancellationToken.None);

        Assert.Empty(_fixture.Registry.State.Groups);
        VerifyNothingLinked();
        Assert.Contains(_logger.Lines, line => line.Contains("at the next sync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefreshedSeriesJoinsByItsEpisodes()
    {
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Season, TestItems.Season("ss1", "s", 1));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2) with { SeasonId = "ss1" });
        await _fixture.Sync.AddServerAsync("A", "shows");
        await _fixture.SyncAndMaterializeAsync();
        var series = new Series { Id = Guid.NewGuid(), Name = "Show" };
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id };
        _fixture.Library.Setup(library => library.GetItemById(series.Id)).Returns(series);
        _fixture.Library.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([episode]);
        _fixture.LocalCatalog.Items.Add(new CatalogItem
        {
            ServerId = "local",
            ItemId = episode.Id.ToString("N"),
            Kind = ContentKind.Episode,
            SeriesId = series.Id.ToString("N"),
            Title = "Show",
            Year = 2020,
            Ids = new ContentIds(null, null, "71"),
            SeasonNumber = 1,
            EpisodeNumber = 2
        });

        await Linker().LinkAsync(series.Id, CancellationToken.None);

        Assert.Equal([episode.Id], Assert.Single(_fixture.Registry.State.Groups).LocalItemIds);
        VerifyLinked([episode.Id], Times.Once());
    }

    [Fact]
    public async Task NothingHappensWithoutAFederatedFolder()
    {
        _fixture.Sync.Store.Configuration.FederatedFolderPath = string.Empty;

        await Linker().LinkAsync(AddLocalFilm(), CancellationToken.None);

        VerifyNothingLinked();
    }

    [Fact]
    public void APlannedGroupTakesThePlaceOfTheGroupWithItsId()
    {
        var local = Guid.NewGuid();
        var other = Group("g2", ["c", "d"]);

        var groups = LocalTitleLinker.Replace([Group("g1", ["a", "b"]), other], [Group("g1", ["a", "b"], local)], new HashSet<string> { "a", "b", "c", "d" });

        Assert.NotNull(groups);
        Assert.Equal(["g2", "g1"], groups.Select(group => group.Id));
        Assert.Same(other, groups[0]);
        Assert.Equal([local], groups[1].LocalItemIds);
    }

    [Fact]
    public void APlannedGroupWithAStubNotWrittenYetIsRefused()
    {
        Assert.Null(LocalTitleLinker.Replace([], [Group("g1", ["a", "new"], Guid.NewGuid())], new HashSet<string> { "a" }));
    }

    [Theory]
    [InlineData("g1")]
    [InlineData("g9")]
    public void AGroupThatWouldLoseAStubOrItsIdIsRefused(string plannedId)
    {
        var current = Group("g1", ["a", "b", "c"]);
        var planned = Group(plannedId, ["a", "b"], Guid.NewGuid());

        Assert.Null(LocalTitleLinker.Replace([current], [planned], new HashSet<string> { "a", "b", "c" }));
    }
}
