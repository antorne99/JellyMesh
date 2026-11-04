using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;
using static Jellyfin.Plugin.JellyMesh.Tests.Dedup.Fixtures;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

/// <summary>
/// The media matrix of <c>tests/Integration/README.md</c>, resolved as one catalog. Remote names, years, IDs and video
/// streams are those the 12.4 test servers really return. Local items have no quality to compare, so they carry none.
/// </summary>
public class MediaMatrixTests
{
    private const string SerieDivisa = "series|tmdb:999000101";
    private const string SerieSenzaId = "series|title:serie senza id|2026";
    private const string SerieDoppia = "series|tmdb:999000201";
    private const string SondaMuta = "movie|title:sonda muta|2026";

    private static readonly ContentIds _serieDivisaIds = new("999000101", "tt99000101", "999000102");
    private static readonly ContentIds _serieDoppiaIds = new("999000201", "tt99000201", "999000202");

    private static readonly CatalogItem[] _local =
    [
        Movie(Local, "Elephants Dream", 2006, new ContentIds("9761", "tt0807840")),
        Movie(Local, "Sintel", 2010, new ContentIds("45745", "tt1727587")),
        Movie(Local, "Sonda Locale Senza ID (2026)", 2026, null).Lasting(1),
        Episode(Local, "JM Serie Divisa", 2026, _serieDivisaIds, 1, 1),
        Episode(Local, "JM Serie Divisa", 2026, _serieDivisaIds, 1, 2),
        Episode(Local, "JM Serie Divisa", 2026, _serieDivisaIds, 1, 3),
        Episode(Local, "Serie Senza ID", 2026, null, 1, 1),
        Movie(Local, "Sonda Trama", 2026, null).Lasting(1).Telling("Un guardiano del faro scopre un messaggio nella sabbia."),
        Double(Local, 1, 2),
        Episode(Local, "JM Serie Doppia", 2026, _serieDoppiaIds, 1, 7),
    ];

    private static readonly CatalogItem[] _standard =
    [
        Movie(Standard, "Tears of Steel", 2012, new ContentIds("133701", "tt2285752"), Quality(720, 2731241)),
        Movie(Standard, "JM Confronto Risoluzione", 2026, new ContentIds("999000002", "tt99000002"), Quality(720, 5969220)),
        Movie(Standard, "JM Confronto HDR", 2026, new ContentIds("999000003", "tt99000003"), Quality(1080, 5917850, "hevc")),
        Movie(Standard, "JM Confronto Bitrate", 2026, new ContentIds("999000004", "tt99000004"), Quality(1080, 2782266)),
        Movie(Standard, "JM Confronto Codec", 2026, new ContentIds("999000005", "tt99000005"), Quality(1080, 2749552)),
        Movie(Standard, "JM Versioni Multiple", 2026, new ContentIds("999000006", "tt99000006"), Quality(1080, 3796883), Quality(720, 1882385)),
        Movie(Standard, "JM Audio e Sottotitoli", 2026, new ContentIds("999000007", "tt99000007"), Quality(1080, 3699146)),
        Movie(Standard, "Sonda Senza ID (2026)", 2026, null, Quality(720, 1890612)).Lasting(1),
        Movie(Standard, "L'Éclisse Fantasma (2026)", 2026, null, Quality(720, 1937326)).Lasting(1),
        Movie(Standard, "Titolo Omonimo (2024)", 2024, null, Quality(720, 1913927)).Lasting(1),
        Movie(Standard, "Sonda Locale Senza ID (2026)", 2026, null, Quality(1080, 3956572)).Lasting(1),
        Episode(Standard, "JM Serie Divisa", 2026, _serieDivisaIds, 1, 3, Quality(720, 1861727)),
        Episode(Standard, "JM Serie Divisa", 2026, _serieDivisaIds, 1, 4, Quality(720, 1862464)),
        Episode(Standard, "JM Serie Divisa", 2026, _serieDivisaIds, 1, 5, Quality(720, 1914681)),
        Episode(Standard, "JM Serie Divisa", 2026, _serieDivisaIds, 1, 6, Quality(720, 1926930)),
        Episode(Standard, "Serie Senza ID", 2026, null, 1, 2, Quality(720, 1901721)),
        Movie(Standard, "JM Copia Senza ID", 2026, new ContentIds("999000008", "tt99000008"), Quality(720, 1923375)).Lasting(1),
        Movie(Standard, "JM ID Parziali", 2026, new ContentIds("999000009", "tt99000009"), Quality(720, 1505812)),
        Movie(Standard, "La La Sonda Muta", 2026, null, Quality(720, 1931793)).Lasting(1).Telling("Un pianista insegue un sogno tra le colline della California."),
        Movie(Standard, "Sonda Trama", 2026, null, Quality(1080, 3550518)).Lasting(1).Telling("Due fratelli riaprono una vecchia officina in montagna."),
        Episode(Standard, "JM Serie Doppia", 2026, _serieDoppiaIds, 1, 1, Quality(720, 1927105)),
        Episode(Standard, "JM Serie Doppia", 2026, _serieDoppiaIds, 1, 2, Quality(720, 1905737)),
        Double(Standard, 3, 4, Quality(720, 1921192)),
        Double(Standard, 5, 6, Quality(720, 1924002)),
        Double(Standard, 7, 8, Quality(720, 1936353)),
        Double(Standard, 9, 10, Quality(720, 1848142)),
    ];

    private static readonly CatalogItem[] _jellyMesh =
    [
        Movie(RemoteJellyMesh, "JM Solo JellyMesh", 2026, new ContentIds("999000001", "tt99000001"), Quality(720, 2950918)),
        Movie(RemoteJellyMesh, "Sintel", 2010, new ContentIds("45745", "tt1727587"), Quality(1080, 4766796, "hevc")),
        Movie(RemoteJellyMesh, "JM Confronto Risoluzione", 2026, new ContentIds("999000002", "tt99000002"), Quality(1080, 2767876)),
        Movie(RemoteJellyMesh, "JM Confronto HDR", 2026, new ContentIds("999000003", "tt99000003"), Quality(1080, 2931727, "hevc", hdr: true)),
        Movie(RemoteJellyMesh, "JM Confronto Bitrate", 2026, new ContentIds("999000004", "tt99000004"), Quality(1080, 5938499)),
        Movie(RemoteJellyMesh, "JM Confronto Codec", 2026, new ContentIds("999000005", "tt99000005"), Quality(1080, 2649413, "av1")),
        Movie(RemoteJellyMesh, "Sonda Senza ID (2026)", 2026, null, Quality(1080, 3834883)).Lasting(1),
        Movie(RemoteJellyMesh, "Eclisse Fantasma (2026)", 2026, null, Quality(1080, 3928844)).Lasting(1),
        Movie(RemoteJellyMesh, "Titolo Omonimo (2025)", 2025, null, Quality(720, 1938863)).Lasting(1),
        Episode(RemoteJellyMesh, "JM Serie Divisa", 2026, _serieDivisaIds, 1, 5, Quality(1080, 3787771)),
        Episode(RemoteJellyMesh, "JM Serie Divisa", 2026, _serieDivisaIds, 1, 6, Quality(1080, 3907226)),
        Episode(RemoteJellyMesh, "JM Serie Divisa", 2026, _serieDivisaIds, 2, 1, Quality(720, 1867382)),
        Episode(RemoteJellyMesh, "JM Serie Divisa", 2026, _serieDivisaIds, 2, 2, Quality(720, 1871427)),
        Episode(RemoteJellyMesh, "Serie Senza ID", 2026, null, 1, 2, Quality(1080, 3861664)),
        Movie(RemoteJellyMesh, "JM Copia Senza ID (2026)", 2026, null, Quality(1080, 3927984)).Lasting(1),
        Movie(RemoteJellyMesh, "JM ID Parziali", 2026, new ContentIds("999000009", "tt99000010"), Quality(1080, 3049281)),
        Movie(RemoteJellyMesh, "Sonda Muta", 2026, null, Quality(1080, 3913810)).Lasting(1).Telling("Una spedizione polare perde i contatti con la base dopo una tempesta."),
        Double(RemoteJellyMesh, 5, 6, Quality(1080, 3906376)),
        Episode(RemoteJellyMesh, "JM Serie Doppia", 2026, _serieDoppiaIds, 1, 10, Quality(1080, 3262028)),
    ];

    private static readonly DedupResult _result = DedupEngine.Resolve(_local, [.. _standard, .. _jellyMesh]);

    [Theory]
    [InlineData("movie|tmdb:133701", Standard, 720, null, 0)] // Tears of Steel: remote only
    [InlineData("movie|tmdb:999000001", RemoteJellyMesh, 720, null, 0)] // JM Solo JellyMesh: remote only
    [InlineData("movie|tmdb:999000002", RemoteJellyMesh, 1080, Standard, 720)] // Resolution beats bitrate
    [InlineData("movie|tmdb:999000003", RemoteJellyMesh, 1080, Standard, 1080)] // HDR beats bitrate
    [InlineData("movie|tmdb:999000004", RemoteJellyMesh, 1080, Standard, 1080)] // Bitrate alone
    [InlineData("movie|tmdb:999000005", RemoteJellyMesh, 1080, Standard, 1080)] // Codec within the tolerance, 3.6% apart
    [InlineData("movie|tmdb:999000006", Standard, 1080, Standard, 720)] // Two versions on one server
    [InlineData("movie|tmdb:999000007", Standard, 1080, null, 0)] // Audio and subtitles
    [InlineData("movie|title:sonda senza id|2026", RemoteJellyMesh, 1080, Standard, 720)] // Same title and year, the runtime confirms
    [InlineData("movie|title:eclisse fantasma|2026", RemoteJellyMesh, 1080, Standard, 720)] // Same after normalization
    [InlineData("movie|title:titolo omonimo|2024", Standard, 720, null, 0)] // Same title, other year
    [InlineData("movie|title:titolo omonimo|2025", RemoteJellyMesh, 720, null, 0)] // Same title, other year
    [InlineData("movie|tmdb:999000008", RemoteJellyMesh, 1080, Standard, 720)] // A copy without IDs joins the one with IDs
    [InlineData("movie|tmdb:999000009", RemoteJellyMesh, 1080, Standard, 720)] // One shared ID, the other differs
    [InlineData(SondaMuta + "|remoto-standard:item-La La Sonda Muta", Standard, 720, null, 0)] // Equal without articles, plots differ
    [InlineData(SondaMuta + "|remoto-jellymesh:item-Sonda Muta", RemoteJellyMesh, 1080, null, 0)]
    [InlineData("movie|title:sonda trama|2026|remoto-standard:item-Sonda Trama", Standard, 1080, null, 0)] // A local copy hides nothing
    [InlineData(SerieDoppia + "|S1E3", Standard, 720, null, 0)] // A remote double covers both its episodes
    [InlineData(SerieDoppia + "|S1E4", Standard, 720, null, 0)]
    [InlineData(SerieDoppia + "|S1E5", RemoteJellyMesh, 1080, Standard, 720)] // Two doubles
    [InlineData(SerieDoppia + "|S1E6", RemoteJellyMesh, 1080, Standard, 720)]
    [InlineData(SerieDoppia + "|S1E8", Standard, 720, null, 0)] // E07 is local, and the double still provides E08
    [InlineData(SerieDoppia + "|S1E9", Standard, 720, null, 0)]
    [InlineData(SerieDoppia + "|S1E10", RemoteJellyMesh, 1080, Standard, 720)] // A better single against the double
    [InlineData(SerieDivisa + "|S1E4", Standard, 720, null, 0)]
    [InlineData(SerieDivisa + "|S1E5", RemoteJellyMesh, 1080, Standard, 720)]
    [InlineData(SerieDivisa + "|S1E6", RemoteJellyMesh, 1080, Standard, 720)]
    [InlineData(SerieDivisa + "|S2E1", RemoteJellyMesh, 720, null, 0)]
    [InlineData(SerieDivisa + "|S2E2", RemoteJellyMesh, 720, null, 0)]
    [InlineData(SerieSenzaId + "|S1E2", RemoteJellyMesh, 1080, Standard, 720)]
    public void RemoteRows_HaveTheExpectedWinnerAndBackup(
        string key,
        string winnerServer,
        int winnerHeight,
        string? backupServer,
        int backupHeight)
    {
        var decision = Find(key);

        Assert.False(decision.IsLocal);
        Assert.Equal((winnerServer, winnerHeight), (decision.Winner.ServerId, decision.Winner.Quality.Height));
        if (backupServer is null)
        {
            Assert.Empty(decision.Backups);
        }
        else
        {
            var backup = Assert.Single(decision.Backups);
            Assert.Equal((backupServer, backupHeight), (backup.ServerId, backup.Quality.Height));
        }
    }

    [Theory]
    [InlineData("movie|tmdb:45745")] // Sintel: local wins though worse
    [InlineData("movie|title:sonda locale senza id|2026")] // Local wins, the runtime confirms
    [InlineData(SerieDivisa + "|S1E3")] // Local episode also on a remote
    [InlineData(SerieDoppia + "|S1E1")] // A local double covers both its episodes
    [InlineData(SerieDoppia + "|S1E2")]
    [InlineData(SerieDoppia + "|S1E7")] // A local episode wins over a remote double
    public void LocalRows_WithARemoteCopy_MaterializeNothing(string key)
    {
        var decision = Find(key);

        Assert.True(decision.IsLocal);
        Assert.Null(decision.Winner);
        Assert.Empty(decision.Backups);
    }

    [Theory]
    [InlineData("movie|tmdb:9761")] // Elephants Dream
    [InlineData(SerieDivisa + "|S1E1")]
    [InlineData(SerieDivisa + "|S1E2")]
    [InlineData(SerieSenzaId + "|S1E1")]
    public void LocalOnlyRows_HaveNoDecision(string key)
    {
        Assert.DoesNotContain(_result.Decisions, decision => decision.Identity.Key == key);
    }

    [Fact]
    public void EveryTitleIsAccountedFor()
    {
        Assert.Equal(36, _result.Decisions.Count);
        Assert.Equal(30, _result.Decisions.Count(decision => decision.Winner is not null));
    }

    [Fact]
    public void Matrix_RaisesOnlyTheExpectedIssues()
    {
        Assert.Equal(
            [
                "ConflictingProviderIds movie|tmdb:999000009",
                $"PossibleDuplicate {SondaMuta}|remoto-jellymesh:item-Sonda Muta, {SondaMuta}|remoto-standard:item-La La Sonda Muta",
                "PossibleDuplicate movie|title:sonda trama|2026|locale:item-Sonda Trama, movie|title:sonda trama|2026|remoto-standard:item-Sonda Trama"
            ],
            _result.Issues.Select(issue => $"{issue.Kind} {string.Join(", ", issue.WorkKeys)}"));
    }

    [Fact]
    public void SeriesEpisodes_ShareOneWorkKey()
    {
        Assert.Equal(
            new[] { SerieDivisa, SerieDoppia, SerieSenzaId }.Order(StringComparer.Ordinal),
            _result.Decisions
                .Where(decision => decision.Identity.Kind == ContentKind.Episode)
                .Select(decision => decision.Identity.WorkKey)
                .Distinct()
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Codec_RowIsInsideTheTolerance()
    {
        var decision = Find("movie|tmdb:999000005");

        Assert.NotNull(decision.Winner);
        Assert.Equal("av1", decision.Winner.Quality.Codec);
        Assert.True(decision.Winner.Quality.BitRate < decision.Backups[0].Quality.BitRate);
        Assert.InRange(2649413d / 2749552d, 0.95, 1);
    }

    [Fact]
    public void Hdr_RowWinsWithTheLowerBitrate()
    {
        var decision = Find("movie|tmdb:999000003");

        Assert.NotNull(decision.Winner);
        Assert.True(decision.Winner.Quality.IsHdr);
        Assert.True(decision.Winner.Quality.BitRate < decision.Backups[0].Quality.BitRate);
    }

    [Fact]
    public void MultipleVersions_RowKeepsBothVersionsOfOneItem()
    {
        var decision = Find("movie|tmdb:999000006");

        Assert.NotNull(decision.Winner);
        Assert.Equal(decision.Winner.ItemId, decision.Backups[0].ItemId);
        Assert.NotEqual(decision.Winner.MediaSourceId, decision.Backups[0].MediaSourceId);
    }

    [Fact]
    public void Result_DoesNotDependOnTheOrderOfItemsOrVersions()
    {
        var expected = Fingerprint(_result);
        var random = new Random(4021);

        for (var round = 0; round < 200; round++)
        {
            var remote = Shuffled(_standard.Concat(_jellyMesh), random)
                .Select(item => item with { Versions = Shuffled(item.Versions, random) });

            Assert.Equal(expected, Fingerprint(DedupEngine.Resolve(Shuffled(_local, random), remote)));
        }
    }

    [Theory]
    [MemberData(nameof(ContestedRows))]
    public void ContestedRows_DoNotDependOnSourceOrder(string title)
    {
        CatalogItem[] items = [.. _standard.Concat(_jellyMesh).Where(item => Row(item) == title)];
        var expected = Fingerprint(DedupEngine.Resolve(_local, items));

        Assert.True(items.Length > 1);
        foreach (var order in Permutations(items))
        {
            Assert.Equal(expected, Fingerprint(DedupEngine.Resolve(_local, order)));
        }
    }

    public static IEnumerable<object[]> ContestedRows() =>
        _standard.Concat(_jellyMesh)
            .GroupBy(Row)
            .Where(group => group.Count() > 1)
            .Select(group => new object[] { group.Key });

    private static CatalogItem Double(string server, int first, int last, params SourceQuality[] versions) =>
        Episode(server, "JM Serie Doppia", 2026, _serieDoppiaIds, 1, first, versions) with { EpisodeNumberEnd = last };

    private static string Row(CatalogItem item) => $"{item.Title} {item.SeasonNumber}x{item.EpisodeNumber}";

    private static FederationDecision Find(string key) => _result.Decisions.Single(decision => decision.Identity.Key == key);
}
