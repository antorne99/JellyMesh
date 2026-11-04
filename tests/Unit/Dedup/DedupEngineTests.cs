using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;
using static Jellyfin.Plugin.JellyMesh.Tests.Dedup.Fixtures;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

public class DedupEngineTests
{
    private static readonly ContentIds _filmIds = new("1", "tt1");
    private static readonly ContentIds _seriesIds = new("100", "tt100", "200");
    private static readonly SourceQuality _hd = Quality(720, 2000000);
    private static readonly SourceQuality _fullHd = Quality(1080, 4000000);

    [Fact]
    public void Local_WinsEvenWhenWorse()
    {
        var result = DedupEngine.Resolve(
            [Movie(Local, "Film", 2020, _filmIds)],
            [Movie(Standard, "Film", 2020, _filmIds, Quality(2160, 9000000, "av1", hdr: true))]);

        var decision = Assert.Single(result.Decisions);
        Assert.True(decision.IsLocal);
        Assert.Null(decision.Winner);
        Assert.Empty(decision.Backups);
    }

    [Fact]
    public void Local_HasNoQualityToCompare()
    {
        var local = Movie(Local, "Film", 2020, _filmIds);

        Assert.Empty(local.Versions);
        Assert.True(Assert.Single(DedupEngine.Resolve([local], [Movie(Standard, "Film", 2020, _filmIds, _hd)]).Decisions).IsLocal);
    }

    [Fact]
    public void OneRemote_IsMaterializedWithoutBackups()
    {
        var result = DedupEngine.Resolve([], [Movie(Standard, "Film", 2020, _filmIds, _hd)]);

        var decision = Assert.Single(result.Decisions);
        Assert.False(decision.IsLocal);
        Assert.Equal(Standard, decision.Winner?.ServerId);
        Assert.Empty(decision.Backups);
    }

    [Fact]
    public void SeveralRemotes_BestQualityWinsAndTheOthersBecomeBackups()
    {
        var result = DedupEngine.Resolve([], [
            Movie("server-a", "Film", 2020, _filmIds, _hd),
            Movie("server-b", "Film", 2020, _filmIds, Quality(2160, 8000000)),
            Movie("server-c", "Film", 2020, _filmIds, _fullHd),
        ]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal("server-b", decision.Winner?.ServerId);
        Assert.Equal(["server-c", "server-a"], decision.Backups.Select(backup => backup.ServerId));
    }

    [Fact]
    public void LocalArrivingLater_RemovesTheWinner_AndLocalRemoved_BringsItBack()
    {
        CatalogItem[] remote = [Movie(Standard, "Film", 2020, _filmIds, _hd)];
        var local = Movie(Local, "Film", 2020, _filmIds);

        Assert.NotNull(Assert.Single(DedupEngine.Resolve([], remote).Decisions).Winner);
        Assert.Null(Assert.Single(DedupEngine.Resolve([local], remote).Decisions).Winner);
        Assert.NotNull(Assert.Single(DedupEngine.Resolve([], remote).Decisions).Winner);
    }

    [Fact]
    public void LocalOnlyTitles_ProduceNoDecision()
    {
        Assert.Empty(DedupEngine.Resolve([Movie(Local, "Film", 2020, _filmIds)], []).Decisions);
    }

    [Fact]
    public void PartialSeries_CompletedPerEpisode_EachWithItsOwnSource()
    {
        var result = DedupEngine.Resolve(
            [Episode(Local, "Serie", 2020, _seriesIds, 1, 1)],
            [
                Episode(Standard, "Serie", 2020, _seriesIds, 1, 1, _fullHd),
                Episode(Standard, "Serie", 2020, _seriesIds, 1, 2, _fullHd),
                Episode(Standard, "Serie", 2020, _seriesIds, 1, 3, _hd),
                Episode(RemoteJellyMesh, "Serie", 2020, _seriesIds, 1, 2, _hd),
                Episode(RemoteJellyMesh, "Serie", 2020, _seriesIds, 1, 3, _fullHd),
            ]);

        Assert.Equal(3, result.Decisions.Count);
        Assert.True(Find(result, 1, 1).IsLocal);
        Assert.Equal(Standard, Find(result, 1, 2).Winner?.ServerId);
        Assert.Equal(RemoteJellyMesh, Find(result, 1, 3).Winner?.ServerId);
        Assert.Single(result.Decisions.Select(decision => decision.Identity.WorkKey).Distinct());
    }

    [Fact]
    public void Episodes_WithTheSameNumbersInOtherSeasonsOrSeries_AreDifferent()
    {
        var result = DedupEngine.Resolve([], [
            Episode(Standard, "Serie", 2020, _seriesIds, 1, 1, _hd),
            Episode(Standard, "Serie", 2020, _seriesIds, 2, 1, _hd),
            Episode(Standard, "Altra", 2020, new ContentIds("101"), 1, 1, _hd),
        ]);

        Assert.Equal(3, result.Decisions.Count);
    }

    [Fact]
    public void Episodes_UseTheSeriesIds_WhateverTheEpisodeTitles()
    {
        var result = DedupEngine.Resolve([], [
            Episode(Standard, "Serie", 2020, new ContentIds(Tvdb: "200"), 1, 1, _hd),
            Episode(RemoteJellyMesh, "Series Renamed", 2019, new ContentIds(Tvdb: "200"), 1, 1, _fullHd),
        ]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(MatchKind.ProviderId, decision.Identity.Match);
        Assert.Equal((1, 1), (decision.Identity.SeasonNumber, decision.Identity.EpisodeNumber));
    }

    [Fact]
    public void Episodes_WithoutSeriesIds_MatchOnSeriesTitleAndYear()
    {
        var result = DedupEngine.Resolve([], [
            Episode(Standard, "La Serie (2020)", 2020, null, 1, 1, _hd),
            Episode(RemoteJellyMesh, "Serie", 2020, null, 1, 1, _fullHd),
            Episode(RemoteJellyMesh, "Serie", 2021, null, 1, 1, _fullHd),
        ]);

        Assert.Equal(2, result.Decisions.Count);
        Assert.All(result.Decisions, decision => Assert.Equal(MatchKind.TitleYear, decision.Identity.Match));
    }

    [Fact]
    public void Series_AreLinkedAcrossEpisodes_NotOnlyWithinOne()
    {
        // S01E05 exists only on the two servers with one ID each. The series is linked by the local S01E01 with both.
        var result = DedupEngine.Resolve(
            [Episode(Local, "Serie", 2020, new ContentIds("100", Tvdb: "200"), 1, 1)],
            [
                Episode(Standard, "Serie", 2020, new ContentIds("100"), 1, 5, _hd),
                Episode(RemoteJellyMesh, "Serie", 2020, new ContentIds(Tvdb: "200"), 1, 5, _fullHd),
            ]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(RemoteJellyMesh, decision.Winner?.ServerId);
        Assert.Equal(Standard, Assert.Single(decision.Backups).ServerId);
    }

    [Fact]
    public void Episode_WithoutNumbers_StandsAloneAndIsImported()
    {
        var result = DedupEngine.Resolve(
            [Episode(Local, "Serie", 2020, _seriesIds, null, null)],
            [Episode(Standard, "Serie", 2020, _seriesIds, 1, null, _hd), Episode(RemoteJellyMesh, "Serie", 2020, _seriesIds, null, 3, _hd)]);

        Assert.Equal(2, result.Decisions.Count);
        Assert.All(result.Decisions, decision =>
        {
            Assert.Equal(MatchKind.ItemOnly, decision.Identity.Match);
            Assert.NotNull(decision.Winner);
            Assert.StartsWith("series|tmdb:100", decision.Identity.WorkKey, System.StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Item_WithNeitherIdsNorTitle_StandsAloneAndIsImported()
    {
        var result = DedupEngine.Resolve([], [
            Movie(Standard, " ", 2020, null, _hd),
            Movie(RemoteJellyMesh, " ", 2020, null, _hd),
        ]);

        Assert.Equal(2, result.Decisions.Count);
        Assert.All(result.Decisions, decision => Assert.Equal(MatchKind.ItemOnly, decision.Identity.Match));
    }

    [Fact]
    public void Federated_RemoteItemsAreNeverImported()
    {
        var federated = Movie(RemoteJellyMesh, "Film", 2020, _filmIds, _fullHd) with { IsFederated = true };

        Assert.Empty(DedupEngine.Resolve([], [federated]).Decisions);

        var decision = Assert.Single(DedupEngine.Resolve([], [federated, Movie(Standard, "Film", 2020, _filmIds, _hd)]).Decisions);
        Assert.Equal(Standard, decision.Winner?.ServerId);
        Assert.Empty(decision.Backups);
    }

    [Fact]
    public void Federated_RemoteItemsDoNotLinkOthers()
    {
        var bridge = Movie(RemoteJellyMesh, "Film", 2020, new ContentIds("1", "tt1"), _hd) with { IsFederated = true };

        var result = DedupEngine.Resolve([], [
            bridge,
            Movie(Standard, "Film", 2020, new ContentIds("1"), _hd),
            Movie("server-c", "Film", 2020, new ContentIds(Imdb: "tt1"), _hd),
        ]);

        Assert.Equal(2, result.Decisions.Count);
    }

    [Fact]
    public void Federated_LocalStubIsNotALocalCopy()
    {
        var stub = Movie(Local, "Film", 2020, _filmIds) with { IsFederated = true };

        var decision = Assert.Single(DedupEngine.Resolve([stub], [Movie(Standard, "Film", 2020, _filmIds, _hd)]).Decisions);

        Assert.False(decision.IsLocal);
    }

    [Fact]
    public void RemoteItem_WithoutVersions_IsSkipped()
    {
        Assert.Empty(DedupEngine.Resolve([], [Movie(Standard, "Film", 2020, _filmIds)]).Decisions);
    }

    [Fact]
    public void MultipleVersions_OnOneServer_BestWinsAndTheOtherIsABackup()
    {
        var result = DedupEngine.Resolve([], [Movie(Standard, "Film", 2020, _filmIds, _hd, _fullHd)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal((Standard, 1080), (decision.Winner?.ServerId, decision.Winner?.Quality.Height));
        var backup = Assert.Single(decision.Backups);
        Assert.Equal((Standard, 720), (backup.ServerId, backup.Quality.Height));
        Assert.Equal(decision.Winner?.ItemId, backup.ItemId);
        Assert.NotEqual(decision.Winner?.MediaSourceId, backup.MediaSourceId);
    }

    [Fact]
    public void MultipleVersions_AcrossServers_AreRankedTogether()
    {
        var result = DedupEngine.Resolve([], [
            Movie(Standard, "Film", 2020, _filmIds, Quality(2160, 9000000), _hd),
            Movie(RemoteJellyMesh, "Film", 2020, _filmIds, _fullHd),
        ]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal((Standard, 2160), (decision.Winner?.ServerId, decision.Winner?.Quality.Height));
        Assert.Equal(
            [(RemoteJellyMesh, (int?)1080), (Standard, 720)],
            decision.Backups.Select(backup => (backup.ServerId, backup.Quality.Height)));
    }

    [Fact]
    public void MultipleVersions_AsSeparateItemsOnOneServer_AreOneTitle()
    {
        var first = Movie(Standard, "Film", 2020, _filmIds, _hd);
        var second = Movie(Standard, "Film", 2020, _filmIds, _fullHd) with { ItemId = "other-item" };

        var decision = Assert.Single(DedupEngine.Resolve([], [first, second]).Decisions);

        Assert.Equal("other-item", decision.Winner?.ItemId);
        Assert.Equal(first.ItemId, Assert.Single(decision.Backups).ItemId);
    }

    [Fact]
    public void MultipleVersions_LocalStillWins()
    {
        var result = DedupEngine.Resolve(
            [Movie(Local, "Film", 2020, _filmIds)],
            [Movie(Standard, "Film", 2020, _filmIds, _hd, _fullHd)]);

        Assert.True(Assert.Single(result.Decisions).IsLocal);
    }

    [Fact]
    public void SameItemIdsOnTwoServers_StayDistinctSources()
    {
        var result = DedupEngine.Resolve([], [
            Movie(Standard, "Film", 2020, _filmIds, _hd),
            Movie(RemoteJellyMesh, "Film", 2020, _filmIds, _hd),
        ]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(RemoteJellyMesh, decision.Winner?.ServerId);
        Assert.Equal(Standard, Assert.Single(decision.Backups).ServerId);
    }

    [Fact]
    public void Decisions_AreSortedByKey()
    {
        var result = DedupEngine.Resolve([], [
            Movie(Standard, "Zeta", 2020, null, _hd),
            Movie(Standard, "Alfa", 2020, null, _hd),
        ]);

        Assert.Equal(
            ["movie|title:alfa|2020", "movie|title:zeta|2020"],
            result.Decisions.Select(decision => decision.Identity.Key));
    }

    private static FederationDecision Find(DedupResult result, int season, int episode) =>
        result.Decisions.Single(decision =>
            decision.Identity.SeasonNumber == season && decision.Identity.EpisodeNumber == episode);

    [Fact]
    public void ALocalDecisionNamesItsLocalCopiesAndItsRemoteItems()
    {
        var local = new CatalogItem { ServerId = "local", ItemId = "l1", Kind = ContentKind.Movie, Ids = new ContentIds("1") };
        var remote = new CatalogItem
        {
            ServerId = "a",
            ItemId = "r1",
            Kind = ContentKind.Movie,
            Ids = new ContentIds("1"),
            Versions = [new MediaVersion("v", new SourceQuality(1080, false, 1000, "h264"))]
        };

        var decision = Assert.Single(DedupEngine.Resolve([local], [remote]).Decisions);

        Assert.True(decision.IsLocal);
        Assert.Equal(["l1"], decision.LocalItemIds);
        Assert.Equal(new RemoteItemKey("a", "r1"), Assert.Single(decision.RemoteItems));
    }

    [Fact]
    public void AWorkWithLocalEpisodesNamesOneEvenIfNoDecisionIsLocal()
    {
        var ids = new ContentIds(null, null, "7");
        var local = new CatalogItem { ServerId = "local", ItemId = "l1", Kind = ContentKind.Episode, Ids = ids, SeasonNumber = 1, EpisodeNumber = 1 };
        var remote = new CatalogItem
        {
            ServerId = "a",
            ItemId = "r2",
            Kind = ContentKind.Episode,
            Ids = ids,
            SeasonNumber = 1,
            EpisodeNumber = 2,
            Versions = [new MediaVersion("v", new SourceQuality(1080, false, 1000, "h264"))]
        };

        var result = DedupEngine.Resolve([local], [remote]);

        var decision = Assert.Single(result.Decisions);
        Assert.False(decision.IsLocal);
        Assert.Equal("l1", result.LocalItemByWork[decision.Identity.WorkKey]);
    }
}
