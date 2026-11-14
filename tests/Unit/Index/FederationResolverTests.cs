using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Index;

public sealed class FederationResolverTests : IDisposable
{
    private readonly SyncFixture _fixture = new();
    private readonly FakeLocalCatalog _local = new();

    public void Dispose() => _fixture.Dispose();

    private FederationResolver Resolver() => new(_fixture.Index, _local, _fixture.Registry, new InMemoryMergeStore());

    private async Task SyncAsync() => await _fixture.Sync.RunAsync(CancellationToken.None);

    [Fact]
    public async Task ALocalCopyWins()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m", tmdb: "1"));
        await _fixture.AddServerAsync("A", "films");
        await SyncAsync();
        _local.Items.Add(new CatalogItem
        {
            ServerId = "local",
            ItemId = "x",
            Kind = ContentKind.Movie,
            Ids = new ContentIds("1")
        });

        var view = Resolver().Resolve();

        var content = Assert.Single(view.Contents);
        Assert.True(content.IsLocal);
        Assert.Null(content.Winner);
        Assert.Equal(1, view.LocalItemCount);
    }

    [Fact]
    public async Task TheSourceKnowsItsServerStateAndWhenItWasSeen()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        var server = await _fixture.AddServerAsync("Friend", "films");
        await SyncAsync();
        _fixture.Clock.Now = SyncFixture.Start.AddHours(1);
        _fixture.Client.OnPage = (_, _) => RemoteFailure.Unreachable;
        await SyncAsync();

        var winner = Assert.Single(Resolver().Resolve().Contents).Winner!;

        Assert.Equal(server.Id, winner.ServerId);
        Assert.Equal("Friend", winner.ServerName);
        Assert.Equal(ServerSyncState.Stale, winner.ServerState);
        Assert.Equal(SyncFixture.Start, winner.LastSeenUtc);
        Assert.Equal("Film", winner.Title);
    }

    [Fact]
    public async Task IdentitiesAreWorkedOutAcrossServersAndLosersAreBackups()
    {
        var movie = TestItems.Movie("m", tmdb: "1");
        _fixture.Client.Add("films", RemoteItemType.Movie, movie);
        await _fixture.AddServerAsync("A", "films");
        await _fixture.AddServerAsync("B", "films");
        await SyncAsync();

        var content = Assert.Single(Resolver().Resolve().Contents);

        Assert.NotNull(content.Winner);
        var backup = Assert.Single(content.Backups);
        Assert.NotEqual(content.Winner!.ServerId, backup.ServerId);
    }

    [Fact]
    public async Task EpisodesOfOneSeriesOnTwoServersMeetByTheSeries()
    {
        _fixture.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e1", "s", 1, 1));
        await _fixture.AddServerAsync("A", "shows");
        await _fixture.AddServerAsync("B", "shows");
        await SyncAsync();

        var content = Assert.Single(Resolver().Resolve().Contents);

        Assert.Equal(MatchKind.ProviderId, content.Identity.Match);
        Assert.Equal((1, 1), (content.Identity.SeasonNumber, content.Identity.EpisodeNumber));
        Assert.Single(content.Backups);
        Assert.Equal("Show S1E1", content.Winner!.Title);
    }

    // Decision 51: what helps an administrator tell two series apart.
    [Fact]
    public async Task ASeriesCopyToMergeCountsItsEpisodesAndSeasons()
    {
        _fixture.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e1", "s", 1, 1));
        _fixture.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e2", "s", 1, 2));
        await _fixture.AddServerAsync("A", "shows");
        await SyncAsync();
        _local.Items.AddRange(new[] { (1, 1), (1, 2), (2, 1) }.Select(episode => new CatalogItem
        {
            ServerId = "local",
            ItemId = "l" + episode.Item1 + episode.Item2,
            Kind = ContentKind.Episode,
            SeriesId = "ls",
            Title = "Show",
            Year = 2020,
            Ids = new ContentIds("99"),
            SeasonNumber = episode.Item1,
            EpisodeNumber = episode.Item2
        }));

        var copies = Assert.Single(Resolver().Resolve().MergeCases).Titles.SelectMany(title => title.Copies).ToList();

        var local = Assert.Single(copies, copy => copy.Copy.IsLocal);
        Assert.Equal((3, 2), (local.EpisodeCount, local.SeasonCount));
        var remote = Assert.Single(copies, copy => !copy.Copy.IsLocal);
        Assert.Equal((2, 1), (remote.EpisodeCount, remote.SeasonCount));
    }

    [Fact]
    public async Task AFilmCopyToMergeHasNoEpisodeCount()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m", tmdb: "1"));
        await _fixture.AddServerAsync("A", "films");
        await SyncAsync();
        _local.Items.Add(new CatalogItem { ServerId = "local", ItemId = "x", Kind = ContentKind.Movie, Title = "Film", Year = 2020, Ids = new ContentIds("2") });

        var copies = Resolver().Resolve().MergeCases.SelectMany(entry => entry.Titles).SelectMany(title => title.Copies).ToList();

        Assert.Equal(2, copies.Count);
        Assert.All(copies, copy => Assert.Equal(((int?)null, (int?)null), (copy.EpisodeCount, copy.SeasonCount)));
    }

    [Fact]
    public async Task ARemovedServerNoLongerGivesSources()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        var server = await _fixture.AddServerAsync("A", "films");
        await SyncAsync();

        await _fixture.Registry.RemoveAsync(server.Id);

        Assert.Empty(Resolver().Resolve().Contents);
    }

    [Fact]
    public void TheLocalFilmsAwaitingARefreshAreNamed()
    {
        var waiting = Guid.NewGuid();
        _local.Items.Add(new CatalogItem { ServerId = "local", ItemId = waiting.ToString("N"), Kind = ContentKind.Movie, Title = "Nuovo", AwaitsRefresh = true });
        _local.Items.Add(new CatalogItem { ServerId = "local", ItemId = Guid.NewGuid().ToString("N"), Kind = ContentKind.Movie, Title = "Vecchio" });

        Assert.Equal([waiting], Resolver().Resolve().LocalItemsAwaitingRefresh);
    }

    [Fact]
    public void ALocalSeriesAwaitingARefreshIsNamedOnceByItsSeries()
    {
        var series = Guid.NewGuid();
        foreach (var number in new[] { 1, 2 })
        {
            _local.Items.Add(new CatalogItem
            {
                ServerId = "local",
                ItemId = Guid.NewGuid().ToString("N"),
                Kind = ContentKind.Episode,
                SeriesId = series.ToString("N"),
                Title = "Nuova",
                SeasonNumber = 1,
                EpisodeNumber = number,
                AwaitsRefresh = true
            });
        }

        Assert.Equal([series], Resolver().Resolve().LocalItemsAwaitingRefresh);
    }

    private sealed class FakeLocalCatalog : ILocalCatalogReader
    {
        public List<CatalogItem> Items { get; } = [];

        public IReadOnlyList<CatalogItem> Read() => Items;
    }
}
