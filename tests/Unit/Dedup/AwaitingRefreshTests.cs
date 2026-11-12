using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;
using static Jellyfin.Plugin.JellyMesh.Tests.Dedup.Fixtures;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

/// <summary>
/// Decision 214: a new local film whose metadata Jellyfin has not read yet takes part in matching, but the cases it is in
/// wait for that refresh.
/// </summary>
public class AwaitingRefreshTests
{
    private static readonly SourceQuality _hd = Quality(720, 2000000);

    // Without its .nfo, the new copy has only its title, year and a runtime that contradicts the remote one.
    private static CatalogItem NewLocal(bool awaitsRefresh) =>
        Movie(Local, "Arrivo", 2026, null).Lasting(1) with { ItemId = "locale-arrivo", AwaitsRefresh = awaitsRefresh };

    private static CatalogItem Remote(string tmdb, string server = Standard) =>
        Movie(server, "Arrivo", 2026, new ContentIds(tmdb), _hd).Lasting(120) with { ItemId = "arrivo-" + tmdb };

    [Fact]
    public void ARefreshedCopy_IsACase()
    {
        var result = DedupEngine.Resolve([NewLocal(false)], [Remote("5")]);

        Assert.Equal(DedupIssueKind.PossibleDuplicate, Assert.Single(result.MergeCases).Kind);
    }

    [Fact]
    public void ACopyAwaitingItsRefresh_IsNoCaseYet()
    {
        var result = DedupEngine.Resolve([NewLocal(true)], [Remote("5")]);

        Assert.Empty(result.MergeCases);
    }

    [Fact]
    public void TheOtherTitlesOfItsCase_StayACase()
    {
        var result = DedupEngine.Resolve([NewLocal(true)], [Remote("5"), Remote("6", RemoteJellyMesh)]);

        var group = Assert.Single(result.MergeCases);
        Assert.Equal(["movie|tmdb:5", "movie|tmdb:6"], group.Works.Select(work => work.WorkKey));
        Assert.All(group.Works, work => Assert.False(work.HasLocalCopy));
    }

    [Fact]
    public void WaitingChangesNothingElse()
    {
        var waiting = DedupEngine.Resolve([NewLocal(true)], [Remote("5")]);
        var refreshed = DedupEngine.Resolve([NewLocal(false)], [Remote("5")]);

        Assert.Equal(Fingerprint(refreshed), Fingerprint(waiting));
        Assert.Equal(refreshed.LocalItemByWork, waiting.LocalItemByWork);
    }
}
