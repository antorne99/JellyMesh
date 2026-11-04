using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;
using static Jellyfin.Plugin.JellyMesh.Tests.Dedup.Fixtures;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

/// <summary>
/// Decision 13a: a file with <c>IndexNumberEnd</c> covers every episode in its range, and each episode is decided on its
/// own (§4.2 rule 4).
/// </summary>
public class MultiEpisodeTests
{
    private const string Work = "series|tmdb:100";

    private static readonly ContentIds _ids = new("100");
    private static readonly SourceQuality _hd = Quality(720, 2000000);
    private static readonly SourceQuality _fullHd = Quality(1080, 4000000);
    private static readonly SourceQuality _uhd = Quality(2160, 8000000);

    [Fact]
    public void ALocalDouble_SuppressesTheRemoteCopiesOfBothEpisodes()
    {
        var local = Double(Local, 1, 2);

        var result = DedupEngine.Resolve([local], [
            Episode(Standard, "Serie", 2020, _ids, 1, 1, _uhd),
            Episode(Standard, "Serie", 2020, _ids, 1, 2, _uhd),
        ]);

        Assert.Equal(2, result.Decisions.Count);
        Assert.All(result.Decisions, decision =>
        {
            Assert.True(decision.IsLocal);
            Assert.Equal([local.ItemId], decision.LocalItemIds);
        });
    }

    [Fact]
    public void LocalSingles_SuppressARemoteDouble()
    {
        var result = DedupEngine.Resolve(
            [Episode(Local, "Serie", 2020, _ids, 1, 1), Episode(Local, "Serie", 2020, _ids, 1, 2)],
            [Double(Standard, 1, 2, _uhd)]);

        Assert.Equal(2, result.Decisions.Count);
        Assert.All(result.Decisions, decision => Assert.True(decision.IsLocal));
    }

    [Fact]
    public void ALocalDouble_SuppressesARemoteDouble()
    {
        var result = DedupEngine.Resolve([Double(Local, 1, 2)], [Double(Standard, 1, 2, _uhd)]);

        Assert.All(result.Decisions, decision => Assert.True(decision.IsLocal));
    }

    [Fact]
    public void ALocalDouble_LeavesTheOtherEpisodesToTheRemotes()
    {
        var result = DedupEngine.Resolve([Double(Local, 1, 2)], [Episode(Standard, "Serie", 2020, _ids, 1, 3, _hd)]);

        var decision = Assert.Single(result.Decisions);
        Assert.False(decision.IsLocal);
        Assert.Equal(3, decision.Identity.EpisodeNumber);
    }

    [Fact]
    public void ARemoteDouble_IsTheSourceOfBothEpisodes()
    {
        var remote = Double(Standard, 1, 2, _hd);

        var result = DedupEngine.Resolve([], [remote]);

        Assert.Equal([$"{Work}|S1E1", $"{Work}|S1E2"], result.Decisions.Select(decision => decision.Identity.Key));
        Assert.All(result.Decisions, decision =>
        {
            Assert.Equal(remote.ItemId, decision.Winner?.ItemId);
            Assert.Equal([new RemoteItemKey(Standard, remote.ItemId)], decision.RemoteItems);
        });
    }

    [Fact]
    public void AFileCoversEveryEpisodeInItsRange_NotJustTheEnds()
    {
        var result = DedupEngine.Resolve([], [Double(Standard, 3, 6, _hd)]);

        Assert.Equal([3, 4, 5, 6], result.Decisions.Select(decision => decision.Identity.EpisodeNumber));
    }

    [Fact]
    public void WithOneEpisodeLocal_ARemoteDouble_StillProvidesTheOther()
    {
        var remote = Double(Standard, 1, 2, _hd);

        var result = DedupEngine.Resolve([Episode(Local, "Serie", 2020, _ids, 1, 1)], [remote]);

        Assert.True(Find(result, 1).IsLocal);
        Assert.Equal(remote.ItemId, Find(result, 2).Winner?.ItemId);
    }

    [Fact]
    public void Quality_IsChosenPerEpisode()
    {
        var @double = Double(Standard, 1, 2, _fullHd);
        var better = Episode(RemoteJellyMesh, "Serie", 2020, _ids, 1, 2, _uhd);
        var worse = Episode("server-c", "Serie", 2020, _ids, 1, 1, _hd);

        var result = DedupEngine.Resolve([], [@double, better, worse]);

        Assert.Equal(Standard, Find(result, 1).Winner?.ServerId);
        Assert.Equal("server-c", Assert.Single(Find(result, 1).Backups).ServerId);
        Assert.Equal(RemoteJellyMesh, Find(result, 2).Winner?.ServerId);
        Assert.Equal(Standard, Assert.Single(Find(result, 2).Backups).ServerId);
    }

    [Fact]
    public void OverlappingFiles_CompeteOnTheEpisodesTheyShare()
    {
        var first = Double(Standard, 1, 2, _hd);
        var second = Double(RemoteJellyMesh, 2, 3, _fullHd);

        var result = DedupEngine.Resolve([], [first, second]);

        Assert.Equal(first.ItemId, Find(result, 1).Winner?.ItemId);
        Assert.Equal(second.ItemId, Find(result, 2).Winner?.ItemId);
        Assert.Equal(second.ItemId, Find(result, 3).Winner?.ItemId);
    }

    [Fact]
    public void ADoubleOfASeriesWithoutIds_FollowsTheSameRules()
    {
        var local = Episode(Local, "Serie", 2020, null, 1, 1);
        var remote = Episode(Standard, "La Serie", 2020, null, 1, 1, _hd) with { EpisodeNumberEnd = 2 };

        var result = DedupEngine.Resolve([local], [remote]);

        Assert.True(Find(result, 1).IsLocal);
        Assert.Equal(MatchKind.TitleYear, Find(result, 2).Identity.Match);
    }

    [Fact]
    public void ADoubleOfNoKnownSeries_StillCoversBothEpisodes_InOneWork()
    {
        var remote = Episode(Standard, "Serie", null, null, 1, 1, _hd) with { EpisodeNumberEnd = 2 };

        var result = DedupEngine.Resolve([], [remote]);

        Assert.Equal(2, result.Decisions.Count);
        Assert.All(result.Decisions, decision => Assert.Equal(MatchKind.ItemOnly, decision.Identity.Match));
        Assert.Single(result.Decisions.Select(decision => decision.Identity.WorkKey).Distinct());
        Assert.Equal(2, result.Decisions.Select(decision => decision.Identity.Key).Distinct().Count());
    }

    [Theory]
    [InlineData(1)] // Equal to the start
    [InlineData(0)] // Before the start
    [InlineData(int.MaxValue)] // A remote cannot make the engine count to two billion
    public void ABadEnd_IsIgnored(int end)
    {
        var remote = Episode(Standard, "Serie", 2020, _ids, 1, 1, _hd) with { EpisodeNumberEnd = end };

        Assert.Equal(1, Assert.Single(DedupEngine.Resolve([], [remote]).Decisions).Identity.EpisodeNumber);
    }

    [Fact]
    public void AnEndWithoutASeason_IsIgnored()
    {
        var remote = Episode(Standard, "Serie", 2020, _ids, null, 1, _hd) with { EpisodeNumberEnd = 2 };

        Assert.Equal(MatchKind.ItemOnly, Assert.Single(DedupEngine.Resolve([], [remote]).Decisions).Identity.Match);
    }

    [Fact]
    public void AFederatedDouble_IsNeverImported()
    {
        var result = DedupEngine.Resolve([], [Double(Standard, 1, 2, _hd) with { IsFederated = true }]);

        Assert.Empty(result.Decisions);
    }

    [Fact]
    public void ALocalStubOfADouble_IsNotALocalCopy()
    {
        var result = DedupEngine.Resolve([Double(Local, 1, 2) with { IsFederated = true }], [Double(Standard, 1, 2, _hd)]);

        Assert.All(result.Decisions, decision => Assert.False(decision.IsLocal));
    }

    [Fact]
    public void Decisions_DoNotDependOnOrder()
    {
        CatalogItem[] remote =
        [
            Double(Standard, 1, 2, _fullHd),
            Double(RemoteJellyMesh, 2, 3, _hd),
            Episode("server-c", "Serie", 2020, _ids, 1, 2, _uhd),
            Episode("server-d", "Serie", 2020, _ids, 1, 3, _fullHd),
        ];
        CatalogItem[] local = [Episode(Local, "Serie", 2020, _ids, 1, 1)];

        var expected = Fingerprint(DedupEngine.Resolve(local, remote));
        foreach (var order in Permutations(remote))
        {
            Assert.Equal(expected, Fingerprint(DedupEngine.Resolve(local, order)));
        }
    }

    private static CatalogItem Double(string server, int first, int last, params SourceQuality[] versions) =>
        Episode(server, "Serie", 2020, _ids, 1, first, versions) with { ItemId = $"item-{server}-{first}-{last}", EpisodeNumberEnd = last };

    private static FederationDecision Find(DedupResult result, int episode) =>
        result.Decisions.Single(decision => decision.Identity.EpisodeNumber == episode);
}
