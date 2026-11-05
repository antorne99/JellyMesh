using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;
using static Jellyfin.Plugin.JellyMesh.Tests.Dedup.Fixtures;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

/// <summary>
/// Decision 20: the administrator joins titles the rules keep apart. A merge only joins works, so everything else, from
/// quality to the local copy winning, is what the engine does for any merged title.
/// </summary>
public class ManualMergeTests
{
    private const string Third = "terzo";

    private static readonly SourceQuality _sd = Quality(480, 1000000);
    private static readonly SourceQuality _hd = Quality(720, 2000000);
    private static readonly SourceQuality _fullHd = Quality(1080, 4000000);

    private static readonly CatalogItem _a = Movie(Standard, "Catena A", 2020, new ContentIds("1", "tt1"), _hd);
    private static readonly CatalogItem _b = Movie(RemoteJellyMesh, "Catena B", 2020, new ContentIds("1", "tt2"), _fullHd);
    private static readonly CatalogItem _c = Movie(Third, "Catena C", 2020, new ContentIds("2", "tt2"), _sd);

    private const string KeyA = "movie|tmdb:1|imdb:tt1";
    private const string KeyB = "movie|tmdb:1|imdb:tt2";
    private const string KeyC = "movie|tmdb:2|imdb:tt2";

    private static Guid Id(int number) => new(number, 0, 0, new byte[8]);

    private static ManualMerge Merge(int id, params CatalogItem[] items) => new()
    {
        Id = Id(id),
        CreatedUtc = DateTimeOffset.UnixEpoch,
        Copies = items.Select(item => MergedCopy.From(item, false)).ToList()
    };

    private static DedupResult Resolve(IEnumerable<CatalogItem> remote, params ManualMerge[] merges) =>
        DedupEngine.Resolve([], remote, merges);

    private static string Fingerprint(DedupResult result)
    {
        var cases = result.MergeCases.Select(entry => string.Join(
            " / ",
            entry.Works.Select(work => work.WorkKey + "=" + string.Join("+", work.Copies.Select(copy => copy.Item.ServerId + ":" + copy.Item.ItemId)))));
        var merges = result.Merges.Select(merge => $"{merge.Merge.Id} {merge.WorkKey} {merge.IsInEffect} {string.Join("+", merge.Copies.Select(copy => copy.Item?.ItemId))}");
        var items = result.Decisions.Select(decision => string.Join("+", decision.RemoteItems));
        return string.Join('\n', new[] { Fixtures.Fingerprint(result) }.Concat(cases).Concat(merges).Concat(items));
    }

    [Fact]
    public void WithoutAMerge_TheChainStaysApartAndIsACase()
    {
        var result = Resolve([_a, _b, _c]);

        Assert.Equal(3, result.Decisions.Count);
        var group = Assert.Single(result.MergeCases);
        Assert.Equal(DedupIssueKind.ConflictingProviderIds, group.Kind);
        Assert.Equal([KeyA, KeyB, KeyC], group.Works.Select(work => work.WorkKey));
    }

    [Fact]
    public void AMergedChain_IsOneTitle_TheBestQualityWins_AndTheOthersAreBackups()
    {
        var result = Resolve([_a, _b, _c], Merge(1, _a, _b, _c));

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(KeyA, decision.Identity.Key);
        Assert.Equal(MatchKind.Manual, decision.Identity.Match);
        Assert.Equal(1080, decision.Winner!.Quality.Height);
        Assert.Equal([720, 480], decision.Backups.Select(backup => backup.Quality.Height!.Value));
        Assert.Equal(3, decision.RemoteItems.Count);
        Assert.Empty(result.Issues);
        Assert.Empty(result.MergeCases);
    }

    [Fact]
    public void MergingPartOfAChain_LeavesTheRestOut_AlthoughItSharesAnId()
    {
        var result = Resolve([_a, _b, _c], Merge(1, _a, _b));

        Assert.Equal([KeyA, KeyC], result.Decisions.Select(decision => decision.Identity.Key));
        var group = Assert.Single(result.MergeCases);
        Assert.Equal([KeyA, KeyC], group.Works.Select(work => work.WorkKey));
        Assert.Equal([Id(1)], group.Works[0].MergeIds);
        Assert.Empty(group.Works[1].MergeIds);
    }

    [Fact]
    public void CopiesWithoutIds_Merge()
    {
        var first = Movie(Standard, "Sonda Muta", 2026, null, _sd).Lasting(90).Telling("un pianista insegue un sogno");
        var second = Movie(RemoteJellyMesh, "Sonda Muta", 2026, null, _hd).Lasting(90).Telling("una spedizione perde i contatti") with { ItemId = "altro" };
        var apart = Resolve([first, second]);
        Assert.Equal(2, apart.Decisions.Count);
        Assert.Equal(DedupIssueKind.PossibleDuplicate, Assert.Single(apart.MergeCases).Kind);

        var merged = Resolve([first, second], Merge(1, first, second));

        Assert.Equal(720, Assert.Single(merged.Decisions).Winner!.Quality.Height);
        Assert.Empty(merged.Issues);
    }

    [Fact]
    public void ALocalCopyStillWins()
    {
        var local = Movie(Local, "Catena A", 2020, new ContentIds(null, "tt9")) with { ItemId = "locale-a" };
        var remote = Movie(Standard, "Catena A", 2020, new ContentIds("9"), _fullHd);

        var result = DedupEngine.Resolve([local], [remote], [new ManualMerge
        {
            Id = Id(1),
            CreatedUtc = DateTimeOffset.UnixEpoch,
            Copies = [MergedCopy.From(local, true), MergedCopy.From(remote, false)]
        }]);

        var decision = Assert.Single(result.Decisions);
        Assert.True(decision.IsLocal);
        Assert.Equal(["locale-a"], decision.LocalItemIds);
        Assert.Empty(decision.Backups);
    }

    [Fact]
    public void TwoLocalFilms_AreNoCase()
    {
        var first = Movie(Local, "Doppio", 2020, new ContentIds("1"));
        var second = Movie(Local, "Doppio", 2020, new ContentIds("2")) with { ItemId = "altro" };

        var result = DedupEngine.Resolve([first, second], []);

        Assert.Equal(DedupIssueKind.PossibleDuplicate, Assert.Single(result.Issues).Kind);
        Assert.Empty(result.MergeCases);
    }

    [Fact]
    public void TwoLocalFilmsAndARemoteCopy_AreACase_MarkingTheLocalOnes()
    {
        var first = Movie(Local, "Doppio", 2020, new ContentIds("1"));
        var second = Movie(Local, "Doppio", 2020, new ContentIds("2")) with { ItemId = "altro" };
        var remote = Movie(Standard, "Doppio", 2020, new ContentIds("3"), _hd);

        var result = DedupEngine.Resolve([first, second], [remote]);

        var group = Assert.Single(result.MergeCases);
        Assert.Equal([true, true, false], group.Works.Select(work => work.HasLocalCopy));
    }

    [Fact]
    public void TwoTitlesThatBothHaveALocalCopy_AreNoCase()
    {
        var first = Movie(Local, "Doppio", 2020, new ContentIds("1"));
        var second = Movie(Local, "Doppio", 2020, new ContentIds("2")) with { ItemId = "altro" };
        var remote = Movie(Standard, "Doppio", 2020, new ContentIds("2"), _hd);

        Assert.Empty(DedupEngine.Resolve([first, second], [remote]).MergeCases);
    }

    [Fact]
    public void ACopyThatGainsIds_StaysInTheMerge()
    {
        var bare = Movie(RemoteJellyMesh, "Catena B", 2020, null, _fullHd);
        var merge = Merge(1, _a, bare);

        var result = Resolve([_a, bare with { Ids = new ContentIds("7", "tt7") }], merge);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(1080, decision.Winner!.Quality.Height);
        Assert.True(Assert.Single(result.Merges).IsInEffect);
    }

    [Fact]
    public void ACopyWhoseIdsWereCorrected_StaysInTheMerge()
    {
        var merge = Merge(1, _a, _b);

        var result = Resolve([_a, _b with { Ids = new ContentIds("8", "tt8") }], merge);

        Assert.Single(result.Decisions);
    }

    [Fact]
    public void ACopyThatLosesItsYearAndIds_StaysInTheMerge()
    {
        var merge = Merge(1, _a, _b);

        var result = Resolve([_a, _b with { Year = null, Ids = ContentIds.None }], merge);

        Assert.Single(result.Decisions);
        Assert.Equal(MatchKind.Manual, result.Decisions[0].Identity.Match);
        Assert.True(Assert.Single(result.Merges).IsInEffect);
    }

    [Fact]
    public void AnEpisodeWithoutAWork_IsNotTouchedByTheMerges()
    {
        var episode = Episode(Standard, "Serie", null, null, 1, 1, _hd);

        var result = Resolve([_a, _b, episode], Merge(1, _a, _b));

        Assert.Equal(2, result.Decisions.Count);
    }

    [Fact]
    public void ACopyThatMoved_IsFoundByItsIds()
    {
        var merge = Merge(1, _a, _b);

        var result = Resolve([_a, _b with { ItemId = "nuovo-percorso" }], merge);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal("nuovo-percorso", decision.Winner!.ItemId);
        Assert.All(Assert.Single(result.Merges).Copies, copy => Assert.NotNull(copy.Item));
    }

    [Fact]
    public void ACopyThatDisappears_LeavesTheMergeWaiting_AndItAppliesAgainWhenItIsBack()
    {
        var merge = Merge(1, _a, _b);

        var gone = Resolve([_a], merge);

        Assert.Equal("movie|tmdb:1", Assert.Single(gone.Decisions).Identity.Key);
        Assert.Equal(MatchKind.ProviderId, gone.Decisions[0].Identity.Match);
        var applied = Assert.Single(gone.Merges);
        Assert.False(applied.IsInEffect);
        Assert.Equal("movie|tmdb:1", applied.WorkKey);
        Assert.Equal(1, applied.Copies.Count(copy => copy.Item is null));

        Assert.Single(Resolve([_a, _b], merge).Decisions);
    }

    [Fact]
    public void AMergeWithNothingInTheCatalog_ChangesNothing()
    {
        var result = Resolve([_c], Merge(1, _a, _b));

        Assert.Equal("movie|tmdb:2", Assert.Single(result.Decisions).Identity.Key);
        Assert.Null(Assert.Single(result.Merges).WorkKey);
    }

    [Fact]
    public void AMergeOfCopiesTheIdsAlreadyJoin_HasNoEffect()
    {
        var first = Movie(Standard, "Film", 2020, new ContentIds("1"), _hd);
        var second = Movie(RemoteJellyMesh, "Film", 2020, new ContentIds("1"), _fullHd);

        var result = Resolve([first, second], Merge(1, first, second));

        Assert.Equal(MatchKind.ProviderId, Assert.Single(result.Decisions).Identity.Match);
        Assert.False(Assert.Single(result.Merges).IsInEffect);
    }

    [Fact]
    public void MergesThatShareACopy_JoinEverything()
    {
        var result = Resolve([_a, _b, _c], Merge(1, _a, _b), Merge(2, _b, _c));

        Assert.Equal(KeyA, Assert.Single(result.Decisions).Identity.Key);
        Assert.Equal([KeyA, KeyA], result.Merges.Select(merge => merge.WorkKey));
    }

    [Fact]
    public void WhatBelongsToAWork_ComesAlong()
    {
        var sameIds = Movie(Third, "Copia di A", 2020, new ContentIds("1", "tt1"), _sd) with { ItemId = "copia" };

        var result = Resolve([_a, sameIds, _b, _c], Merge(1, _a, _b));

        Assert.Equal(3, result.Decisions.Single(decision => decision.Identity.Key == KeyA).RemoteItems.Count);
    }

    [Fact]
    public void AnUndo_GivesTheSameResultAsNeverHavingMerged()
    {
        Assert.Equal(Fingerprint(Resolve([_a, _b, _c])), Fingerprint(DedupEngine.Resolve([], [_a, _b, _c], [])));
    }

    [Fact]
    public void AnIdThatChangedKeepsTheOtherIdsOfTheMerge_InCanonicalForm()
    {
        var odd = _a with { Ids = new ContentIds(" 1 ", "TT1") };

        Assert.Single(Resolve([odd, _b], Merge(1, _a, _b)).Decisions);
        Assert.Equal("tt1", MergedCopy.From(odd, false).Imdb);
        Assert.Equal("1", MergedCopy.From(odd, false).Tmdb);
    }

    [Fact]
    public void IssuesThatShareATitle_AreOneCase()
    {
        // The chain joins the first two, and the title joins a third with other IDs.
        var other = Movie(Third, "Catena A", 2020, new ContentIds("5", "tt5"), _sd);

        var group = Assert.Single(Resolve([_a, _b, _c, other]).MergeCases);

        Assert.Equal(DedupIssueKind.ConflictingProviderIds, group.Kind);
        Assert.Equal(4, group.Works.Count);
    }

    [Fact]
    public void ACaseOfSeries_IsNotListed_AndEpisodesIgnoreMerges()
    {
        var first = Episode(Standard, "Serie", 2020, new ContentIds("1"), 1, 1, _hd);
        var second = Episode(RemoteJellyMesh, "Serie", 2020, new ContentIds("2"), 1, 1, _fullHd) with { ItemId = "altro" };
        var merge = Merge(1, first, second);

        var result = Resolve([first, second], merge);

        Assert.Equal(2, result.Decisions.Count);
        Assert.Single(result.Issues);
        Assert.Empty(result.MergeCases);
    }

    [Fact]
    public void RemoteItemsAreSorted()
    {
        var result = Resolve([_c, _b, _a], Merge(1, _a, _b, _c));

        var items = Assert.Single(result.Decisions).RemoteItems.Select(item => item.ServerId).ToList();
        Assert.Equal(items.Order(StringComparer.Ordinal), items);
    }

    [Fact]
    public void TheResultDoesNotDependOnTheOrderOfItems()
    {
        var expected = Fingerprint(Resolve([_a, _b, _c], Merge(1, _a, _b)));

        foreach (var items in Permutations<CatalogItem>([_a, _b, _c]))
        {
            Assert.Equal(expected, Fingerprint(Resolve(items, Merge(1, _a, _b))));
        }
    }

    [Fact]
    public void TheResultDoesNotDependOnTheOrderOfTheCopiesOfAMerge()
    {
        var merge = Merge(1, _a, _b, _c);
        var expected = Fingerprint(Resolve([_a, _b, _c], merge));

        foreach (var copies in Permutations(merge.Copies))
        {
            Assert.Equal(expected, Fingerprint(Resolve([_c, _a, _b], merge with { Copies = copies })));
        }
    }

    [Fact]
    public void TheResultDoesNotDependOnTheOrderOfTheMerges()
    {
        var merges = new[] { Merge(1, _a, _b), Merge(2, _b, _c), Merge(3, _a, _c) };
        var expected = Fingerprint(Resolve([_a, _b, _c], merges));

        foreach (var order in Permutations(merges))
        {
            foreach (var items in Permutations<CatalogItem>([_a, _b, _c]))
            {
                Assert.Equal(expected, Fingerprint(Resolve(items, [.. order])));
            }
        }
    }

    [Fact]
    public void AMixedCatalogIsOrderIndependent()
    {
        var bare = Movie(Standard, "Sonda", 2026, null, _sd).Lasting(90).Telling("un pianista insegue un sogno");
        var other = Movie(RemoteJellyMesh, "Sonda", 2026, null, _hd).Lasting(90).Telling("una spedizione perde i contatti") with { ItemId = "altro" };
        var sameIds = Movie(Third, "Copia di A", 2020, new ContentIds("1", "tt1"), _sd) with { ItemId = "copia" };
        var episode = Episode(Third, "Serie", 2020, new ContentIds("1"), 1, 1, _hd);
        var catalog = new[] { _a, _b, _c, bare, other, sameIds, episode };
        var merges = new[] { Merge(2, bare, other), Merge(1, _b, _c) };
        var expected = Fingerprint(Resolve(catalog, merges));

        var random = new Random(20);
        for (var turn = 0; turn < 200; turn++)
        {
            var shuffled = Shuffled(merges, random).Select(merge => merge with { Copies = Shuffled(merge.Copies, random) }).ToArray();
            Assert.Equal(expected, Fingerprint(Resolve(Shuffled(catalog, random), shuffled)));
        }
    }

    [Fact]
    public void TheItemsAStubIsLookedForByDoNotDependOnTheOrderOfARead()
    {
        var expected = Resolve([_a, _b], Merge(1, _a, _b)).Decisions[0].RemoteItems;

        foreach (var items in Permutations<CatalogItem>([_a, _b]))
        {
            Assert.Equal(expected, Resolve(items, Merge(1, _a, _b)).Decisions[0].RemoteItems);
        }
    }
}
