using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Dedup;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Index;

// Row 529: a remote name that may have been cut matches by provider ID only, so that titles equal up to 1000 characters
// are not merged with each other or with a local copy.
public class LongNameIdentityTests
{
    private const int Limit = 1000;
    private const string Artist = "Daft Punk";

    private static readonly DateTimeOffset _now = new(2026, 11, 23, 3, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _length = TimeSpan.FromMinutes(100);
    private static readonly string _atLimit = new('a', Limit);

    public static TheoryData<int> LongLengths() => new(999, 1000, 1001, 1500);

    private static RemoteItem Film(string id, string name, string? tmdb = null) => TestItems.Movie(id, name, 2020) with
    {
        ProviderIds = Ids(tmdb),
        RunTimeTicks = _length.Ticks
    };

    private static RemoteItem Video(string id, string name, string artist, string? tmdb = null) => Film(id, name, tmdb) with { Artists = [artist] };

    private static RemoteItem Series(string name, string? tvdb = null) => TestItems.Series("s", name, 2020) with
    {
        ProviderIds = tvdb is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["Tvdb"] = tvdb }
    };

    private static Dictionary<string, string?> Ids(string? tmdb) => tmdb is null ? [] : new Dictionary<string, string?> { ["Tmdb"] = tmdb };

    private static IReadOnlyList<CatalogItem> Read(int server, params (RemoteItem Item, RemoteItemType Type)[] items) => CatalogItemMapper.ToCatalogItems(new ServerCatalog
    {
        ServerId = new Guid(server, 0, 0, new byte[8]),
        State = ServerSyncState.Complete,
        Items = items.Select(item => RemoteItemMapper.ToIndexedItem(item.Item, item.Type, "library", _now)).ToList()
    });

    private static IReadOnlyList<CatalogItem> Films(int server, params RemoteItem[] films) => Read(server, films.Select(film => (film, RemoteItemType.Movie)).ToArray());

    private static IReadOnlyList<CatalogItem> Videos(int server, params RemoteItem[] videos) => Read(server, videos.Select(video => (video, RemoteItemType.MusicVideo)).ToArray());

    private static IReadOnlyList<CatalogItem> Episodes(int server, RemoteItem series, string seriesName) => Read(
        server,
        (series, RemoteItemType.Series),
        (TestItems.Episode("e", "s", 1, 1, seriesName), RemoteItemType.Episode));

    private static CatalogItem LocalFilm(string name, ContentIds? ids = null) => Fixtures.Movie(Fixtures.Local, name, 2020, ids).Lasting(100);

    private static void AssertImported(int titles, DedupResult result)
    {
        var imported = result.Decisions.Where(decision => decision.RemoteItems.Count > 0).ToList();
        Assert.Equal(titles, imported.Count);
        Assert.All(imported, decision =>
        {
            Assert.NotNull(decision.Winner);
            Assert.Empty(decision.Backups);
            Assert.Empty(decision.LocalItemIds);
        });
        Assert.Empty(result.Issues);
        Assert.Empty(result.MergeCases);
    }

    private static void AssertLocal(DedupResult result)
    {
        var decision = Assert.Single(result.Decisions, decision => decision.RemoteItems.Count > 0);
        Assert.Null(decision.Winner);
        Assert.Single(decision.LocalItemIds);
    }

    [Theory]
    [MemberData(nameof(LongLengths))]
    public void ALongFilmName_IsNotTheLocalFilmItBeginsLike(int length)
        => AssertImported(1, DedupEngine.Resolve([LocalFilm(_atLimit[..Math.Min(length, Limit)])], Films(1, Film("m", new string('a', length)))));

    [Fact]
    public void ACutFilmName_IsNotALocalFilmThatLacksTheSplitPair()
        => AssertImported(1, DedupEngine.Resolve([LocalFilm(_atLimit[..999])], Films(1, Film("m", _atLimit[..999] + "\U0001D11E and more"))));

    [Fact]
    public void FilmsEqualUpToTheLimit_AreTwoTitles()
        => AssertImported(2, DedupEngine.Resolve([], [.. Films(1, Film("m", _atLimit + "one")), .. Films(2, Film("m", _atLimit + "two"))]));

    [Fact]
    public void ALongFilmName_StillMatchesByProviderId()
        => AssertLocal(DedupEngine.Resolve([LocalFilm("Film", new ContentIds("5", null))], Films(1, Film("m", _atLimit + "one", "5"))));

    [Fact]
    public void AFilmNameTooShortToHaveBeenCut_StillMatchesByTitle()
        => AssertLocal(DedupEngine.Resolve([LocalFilm(_atLimit[..998])], Films(1, Film("m", _atLimit[..998]))));

    [Theory]
    [MemberData(nameof(LongLengths))]
    public void ALongSeriesName_IsNotTheLocalSeriesItBeginsLike(int length)
    {
        var name = new string('a', length);
        var local = Fixtures.Episode(Fixtures.Local, _atLimit[..Math.Min(length, Limit)], 2020, null, 1, 1);

        AssertImported(1, DedupEngine.Resolve([local], Episodes(1, Series(name), name)));
    }

    [Fact]
    public void ALongSeriesName_DoesNotGiveWayToTheSeriesNameOfTheEpisode()
        => AssertImported(1, DedupEngine.Resolve([Fixtures.Episode(Fixtures.Local, "Show", 2020, null, 1, 1)], Episodes(1, Series(_atLimit + "one"), "Show")));

    [Fact]
    public void SeriesEqualUpToTheLimit_AreTwoSeries()
        => AssertImported(2, DedupEngine.Resolve([], [.. Episodes(1, Series(_atLimit + "one"), _atLimit + "one"), .. Episodes(2, Series(_atLimit + "two"), _atLimit + "two")]));

    [Fact]
    public void ALongSeriesName_StillMatchesByProviderId()
        => AssertLocal(DedupEngine.Resolve(
            [Fixtures.Episode(Fixtures.Local, "Show", 2020, new ContentIds(null, null, "7"), 1, 1)],
            Episodes(1, Series(_atLimit + "one", "7"), _atLimit + "one")));

    [Theory]
    [MemberData(nameof(LongLengths))]
    public void ALongVideoName_IsNotTheLocalVideoItBeginsLike(int length)
    {
        var local = Fixtures.MusicVideo(Fixtures.Local, _atLimit[..Math.Min(length, Limit)], 2020, null, [Artist]);

        AssertImported(1, DedupEngine.Resolve([local], Videos(1, Video("v", new string('a', length), Artist))));
    }

    [Theory]
    [MemberData(nameof(LongLengths))]
    public void ALongArtist_IsNotTheLocalArtistItBeginsLike(int length)
    {
        var local = Fixtures.MusicVideo(Fixtures.Local, "Aerodynamic", 2020, null, [_atLimit[..Math.Min(length, Limit)]]);

        AssertImported(1, DedupEngine.Resolve([local], Videos(1, Video("v", "Aerodynamic", new string('a', length)))));
    }

    [Fact]
    public void ALongArtist_IsNotLeftOutOfTheArtists()
    {
        var local = Fixtures.MusicVideo(Fixtures.Local, "Aerodynamic", 2020, null, [Artist]);
        var remote = Video("v", "Aerodynamic", Artist) with { Artists = [Artist, _atLimit + "one"] };

        AssertImported(1, DedupEngine.Resolve([local], Videos(1, remote)));
    }

    [Fact]
    public void VideosEqualUpToTheLimit_AreTwoTitles()
        => AssertImported(4, DedupEngine.Resolve([], [
            .. Videos(1, Video("v1", _atLimit + "one", Artist), Video("v2", "Aerodynamic", _atLimit + "one")),
            .. Videos(2, Video("v1", _atLimit + "two", Artist), Video("v2", "Aerodynamic", _atLimit + "two"))
        ]));

    [Fact]
    public void ALongVideoName_StillMatchesByProviderId()
        => AssertLocal(DedupEngine.Resolve(
            [Fixtures.MusicVideo(Fixtures.Local, "Aerodynamic", 2020, new ContentIds("5", null), [Artist])],
            Videos(1, Video("v", _atLimit + "one", _atLimit + "two", "5"))));
}
