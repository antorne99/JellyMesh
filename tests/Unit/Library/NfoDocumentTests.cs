using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class NfoDocumentTests
{
    private static readonly IndexedItem _item = new()
    {
        ItemId = "i",
        Kind = IndexedItemKind.Movie,
        LibraryId = "l",
        LastSeenUtc = DateTimeOffset.UnixEpoch,
        Name = "Title",
        ProductionYear = 2020,
        OriginalTitle = "Original",
        Overview = "Plot",
        Genres = ["Genre"],
        Studios = ["Studio"],
        Tags = ["Tag"],
        Taglines = ["Tagline"],
        OfficialRating = "PG",
        CommunityRating = 7.5f,
        People = [new IndexedPerson("Actor", "Role", "Actor")],
        PremiereDate = new DateTimeOffset(2020, 2, 3, 0, 0, 0, TimeSpan.Zero),
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1", ["Imdb"] = "tt1" }
    };

    private static StubMetadata Protected() => StubMetadata.From(_item, true);

    private static StubMetadata Thin() => StubMetadata.From(_item, false);

    [Fact]
    public void AProtectedFilmHasEverythingAndLockdata()
    {
        var root = XDocument.Parse(NfoDocument.Movie(Protected())).Root!;

        Assert.Equal("movie", root.Name.LocalName);
        Assert.Equal("true", root.Element("lockdata")?.Value);
        Assert.Null(root.Element("lockedfields"));
        Assert.Equal("Plot", root.Element("plot")?.Value);
        Assert.Equal("Genre", root.Element("genre")?.Value);
        Assert.Equal("Actor", root.Element("actor")?.Element("name")?.Value);
        Assert.Equal("2020-02-03", root.Element("premiered")?.Value);
        Assert.Equal(["tmdb", "imdb"], root.Elements("uniqueid").Select(id => id.Attribute("type")!.Value).OrderDescending());
    }

    [Fact]
    public void AStubOfSeveralEpisodesSaysWhereItEnds()
    {
        var root = XDocument.Parse(NfoDocument.Episode(Protected() with { SeasonNumber = 1, EpisodeNumber = 2, EpisodeNumberEnd = 3 })).Root!;

        Assert.Equal("2", root.Element("episode")?.Value);
        Assert.Equal("3", root.Element("episodenumberend")?.Value);
    }

    [Fact]
    public void AStubOfOneEpisodeHasNoEnd()
    {
        var root = XDocument.Parse(NfoDocument.Episode(Protected() with { SeasonNumber = 1, EpisodeNumber = 2 })).Root!;

        Assert.Null(root.Element("episodenumberend"));
    }

    [Fact]
    public void WithoutProtectionARangeComesFromTheFileName()
    {
        var root = XDocument.Parse(NfoDocument.Episode(Thin() with { SeasonNumber = 1, EpisodeNumber = 2, EpisodeNumberEnd = 3 })).Root!;

        Assert.Null(root.Element("episode"));
        Assert.Null(root.Element("episodenumberend"));
    }

    [Fact]
    public void AnEpisodeHasLockdataAndItsNumbers()
    {
        var metadata = Protected() with { SeasonNumber = 1, EpisodeNumber = 2 };

        var root = XDocument.Parse(NfoDocument.Episode(metadata)).Root!;

        Assert.Equal("episodedetails", root.Name.LocalName);
        Assert.Equal("true", root.Element("lockdata")?.Value);
        Assert.Equal("1", root.Element("season")?.Value);
        Assert.Equal("2", root.Element("episode")?.Value);
    }

    [Fact]
    public void ASeriesHasLockedFieldsAndNeverLockdata()
    {
        var root = XDocument.Parse(NfoDocument.Series(Protected())).Root!;

        Assert.Equal("tvshow", root.Name.LocalName);
        Assert.Null(root.Element("lockdata"));
        Assert.Equal("Cast|Genres|ProductionLocations|Studios|Tags|Name|Overview|OfficialRating", root.Element("lockedfields")?.Value);
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("episode")]
    [InlineData("series")]
    public void WithoutProtectionOnlyTitleYearAndIdsAreWritten(string kind)
    {
        var xml = kind switch
        {
            "movie" => NfoDocument.Movie(Thin()),
            "episode" => NfoDocument.Episode(Thin() with { SeasonNumber = 1, EpisodeNumber = 2 }),
            _ => NfoDocument.Series(Thin())
        };

        var names = XDocument.Parse(xml).Root!.Elements().Select(element => element.Name.LocalName).Distinct().Order();

        Assert.Equal(["title", "uniqueid", "year"], names);
    }

    private static readonly IndexedItem _plain = new()
    {
        ItemId = "i",
        Kind = IndexedItemKind.Movie,
        LibraryId = "l",
        LastSeenUtc = DateTimeOffset.UnixEpoch,
        Name = "Title",
        ProductionYear = 2020,
        Overview = "Plot",
        Genres = ["Genre"],
        People = [new IndexedPerson("Actor", "Role", "Actor")],
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1" }
    };

    [Fact]
    public void AProtectedFilmWithoutTrailersIsWrittenAsBefore()
    {
        const string Expected = "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n"
            + "<movie>\n"
            + "  <title>Title</title>\n"
            + "  <year>2020</year>\n"
            + "  <uniqueid type=\"tmdb\">1</uniqueid>\n"
            + "  <plot>Plot</plot>\n"
            + "  <genre>Genre</genre>\n"
            + "  <actor>\n"
            + "    <name>Actor</name>\n"
            + "    <role>Role</role>\n"
            + "    <type>Actor</type>\n"
            + "  </actor>\n"
            + "  <lockdata>true</lockdata>\n"
            + "</movie>";

        Assert.Equal(Expected, NfoDocument.Movie(StubMetadata.From(_plain, true)).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void AProtectedSeriesWithoutTrailersIsWrittenAsBefore()
    {
        const string Expected = "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n"
            + "<tvshow>\n"
            + "  <title>Title</title>\n"
            + "  <year>2020</year>\n"
            + "  <uniqueid type=\"tmdb\">1</uniqueid>\n"
            + "  <plot>Plot</plot>\n"
            + "  <genre>Genre</genre>\n"
            + "  <actor>\n"
            + "    <name>Actor</name>\n"
            + "    <role>Role</role>\n"
            + "    <type>Actor</type>\n"
            + "  </actor>\n"
            + "  <lockedfields>Cast|Genres|ProductionLocations|Studios|Tags|Name|Overview|OfficialRating</lockedfields>\n"
            + "</tvshow>";

        Assert.Equal(Expected, NfoDocument.Series(StubMetadata.From(_plain with { Kind = IndexedItemKind.Series }, true)).ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData(IndexedItemKind.Movie, "lockdata")]
    [InlineData(IndexedItemKind.Series, "lockedfields")]
    public void AProtectedFilmOrSeriesHasItsTrailersInTheKodiForm(IndexedItemKind kind, string last)
    {
        var item = _plain with { Kind = kind, TrailerIds = ["dQw4w9WgXcQ", "a-b_c123456"] };
        var metadata = StubMetadata.From(item, true);

        var root = XDocument.Parse(kind == IndexedItemKind.Movie ? NfoDocument.Movie(metadata) : NfoDocument.Series(metadata)).Root!;

        Assert.Equal(
            ["plugin://plugin.video.youtube/play/?video_id=dQw4w9WgXcQ", "plugin://plugin.video.youtube/play/?video_id=a-b_c123456"],
            root.Elements("trailer").Select(trailer => trailer.Value));
        Assert.Equal(last, root.Elements().Last().Name.LocalName);
    }

    [Fact]
    public void WithoutProtectionAFilmHasNoTrailer()
    {
        var metadata = StubMetadata.From(_plain with { TrailerIds = ["dQw4w9WgXcQ"] }, false);

        Assert.Null(XDocument.Parse(NfoDocument.Movie(metadata)).Root!.Element("trailer"));
    }

    [Fact]
    public void AnEpisodeHasNoTrailer()
    {
        var metadata = StubMetadata.From(_plain with { Kind = IndexedItemKind.Episode, TrailerIds = ["dQw4w9WgXcQ"] }, true);

        Assert.Null(XDocument.Parse(NfoDocument.Episode(metadata)).Root!.Element("trailer"));
    }

    [Fact]
    public void AnIdThatIsNotOneIsNeverWritten()
    {
        var metadata = StubMetadata.From(_plain, true) with { TrailerIds = ["x\"><evil/>", "dQw4w9WgXcQ&a=1"] };

        Assert.Null(XDocument.Parse(NfoDocument.Movie(metadata)).Root!.Element("trailer"));
    }

    [Fact]
    public void AFilmWithoutACollectionIsWrittenAsBefore()
    {
        const string Expected = "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n"
            + "<movie>\n"
            + "  <title>Title</title>\n"
            + "  <year>2020</year>\n"
            + "  <uniqueid type=\"imdb\">tt1</uniqueid>\n"
            + "  <uniqueid type=\"tmdb\">1</uniqueid>\n"
            + "</movie>";

        Assert.Equal(Expected, NfoDocument.Movie(Thin()).ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFilmInACollectionHasASetWithItsName(bool isProtected)
    {
        var metadata = StubMetadata.From(_item, isProtected) with { CollectionName = "Saga <Uno> & \"Due\"\u0001", TmdbCollectionId = "1241" };

        var root = XDocument.Parse(NfoDocument.Movie(metadata)).Root!;

        var set = Assert.Single(root.Elements("set"));
        Assert.Equal("1241", set.Attribute("tmdbcolid")?.Value);
        Assert.Equal("Saga <Uno> & \"Due\"", set.Element("name")?.Value);
        Assert.Equal(isProtected ? "lockdata" : "set", root.Elements().Last().Name.LocalName);
    }

    [Fact]
    public void ACollectionWithoutTmdbIdHasNoAttribute()
    {
        var root = XDocument.Parse(NfoDocument.Movie(Protected() with { CollectionName = "Saga" })).Root!;

        var set = Assert.Single(root.Elements("set"));
        Assert.Empty(set.Attributes());
        Assert.Equal("Saga", set.Element("name")?.Value);
    }

    [Fact]
    public void OnlyFilmsHaveASet()
    {
        var metadata = Protected() with { CollectionName = "Saga", SeasonNumber = 1, EpisodeNumber = 2 };

        Assert.Null(XDocument.Parse(NfoDocument.Episode(metadata)).Root!.Element("set"));
        Assert.Null(XDocument.Parse(NfoDocument.Series(metadata)).Root!.Element("set"));
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("episode")]
    [InlineData("series")]
    public void NoNfoCarriesWatchState(string kind)
    {
        var xml = kind switch
        {
            "movie" => NfoDocument.Movie(Protected()),
            "episode" => NfoDocument.Episode(Protected() with { SeasonNumber = 1, EpisodeNumber = 2 }),
            _ => NfoDocument.Series(Protected())
        };

        var names = XDocument.Parse(xml).Root!.Descendants().Select(element => element.Name.LocalName).ToHashSet();

        foreach (var name in new[] { "playcount", "lastplayed", "watched", "resume", "userrating" })
        {
            Assert.DoesNotContain(name, names);
        }
    }

    [Fact]
    public void UntrustedTextStaysText()
    {
        var hostile = "<script>&\"'\u0001\u0008 end";
        var metadata = Protected() with { Title = hostile, Overview = "]]><lockdata>false</lockdata>" };

        var root = XDocument.Parse(NfoDocument.Movie(metadata)).Root!;

        Assert.Equal("<script>&\"' end", root.Element("title")?.Value);
        Assert.Equal("]]><lockdata>false</lockdata>", root.Element("plot")?.Value);
        Assert.Equal("true", Assert.Single(root.Elements("lockdata")).Value);
    }

    [Fact]
    public void AFilmNameLosesTheYearJellyfinLeftInIt()
    {
        var item = _item with { Name = "Sonda Senza ID (2020)" };

        Assert.Equal("Sonda Senza ID", StubMetadata.From(item, true).Title);
    }
}
