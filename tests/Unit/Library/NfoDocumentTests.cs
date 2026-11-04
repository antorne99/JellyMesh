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
