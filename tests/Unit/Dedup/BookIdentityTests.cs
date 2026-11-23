using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

public class BookIdentityTests
{
    private static BookItem Book(string item, string? title = "Dune", int? year = 1965, string? series = "Dune Saga", string? extension = "epub", params (string Key, string Value)[] ids) => new()
    {
        ServerId = item[..1],
        ItemId = item,
        Title = title,
        Year = year,
        SeriesName = series,
        Extension = extension,
        ProviderIds = ids.ToDictionary(id => id.Key, id => id.Value)
    };

    private static List<string> Pairs(BookItem[] saved, params BookItem[] current)
        => BookIdentity.Pair(saved, current).Select(pair => pair.Saved.ItemId + ">" + pair.Current.ItemId).Order(System.StringComparer.Ordinal).ToList();

    [Fact]
    public void OneSharedIdIsEnoughWhateverTheRest()
    {
        var saved = Book("k1", ids: [("ISBN", "978-1"), ("Google", "g1")]);

        Assert.Equal(
            ["k1>a1", "k1>b1"],
            Pairs([saved], Book("a1", "Other", 2000, null, "pdf", ("isbn", "978-1")), Book("b1", "Else", null, ids: [("Google", "G1"), ("ISBN", "978-2")]), Book("c1", "None", ids: [("ISBN", "978-3")])));
    }

    [Fact]
    public void AnIdOfAnotherProviderOrABlankOneIsNotShared()
    {
        Assert.Empty(Pairs([Book("k1", ids: [("ISBN", "1"), ("Google", " ")])], Book("a1", "Other", ids: [("Google", "1")]), Book("a2", "Other", ids: [("Google", " ")])));
    }

    [Theory]
    [InlineData("DUNE!", 1965, "dune saga", "EPUB", true)]
    [InlineData("Dune (1965)", 1965, "Dune Saga", "epub", true)]
    [InlineData("Dune 2", 1965, "Dune Saga", "epub", false)]
    [InlineData("Dune", 1966, "Dune Saga", "epub", false)]
    [InlineData("Dune", null, "Dune Saga", "epub", false)]
    [InlineData("Dune", 1965, "Other", "epub", false)]
    [InlineData("Dune", 1965, null, "epub", false)]
    [InlineData("Dune", 1965, "Dune Saga", "pdf", false)]
    [InlineData("Dune", 1965, "Dune Saga", null, false)]
    public void WithoutASharedIdTitleYearSeriesAndExtensionMustAllBeEqual(string title, int? year, string? series, string? extension, bool isSame)
    {
        Assert.Equal(isSame ? ["k1>a1"] : [], Pairs([Book("k1")], Book("a1", title, year, series, extension)));
    }

    // As for films, a title tells nothing without a year; no series on either side is the same series.
    [Fact]
    public void ABookWithNoYearMatchesByIdOnlyAndOneWithNoSeriesByTheRest()
    {
        Assert.Empty(Pairs([Book("k1", year: null)], Book("a1", year: null)));
        Assert.Empty(Pairs([Book("k1", " ")], Book("a1", " ")));
        Assert.Equal(["k1>a1"], Pairs([Book("k1", series: null)], Book("a1", series: " ")));
    }

    [Fact]
    public void TwoIdsOfOneProviderKeepTwoBooksApartAndAMissingOneDoesNot()
    {
        var saved = Book("k1", ids: [("ISBN", "1")]);

        Assert.Equal(["k1>b1", "k1>c1"], Pairs([saved], Book("a1", ids: [("isbn", "2")]), Book("b1"), Book("c1", ids: [("Google", "g")])));
    }

    [Fact]
    public void EachSavedBookFindsEveryCopyOfItsOwn()
    {
        Assert.Equal(["k1>a1", "k1>b1", "k2>a2"], Pairs([Book("k1"), Book("k2", "Messiah")], Book("a1"), Book("a2", "Messiah"), Book("b1")));
        Assert.Empty(Pairs([Book("k1"), Book("k2")]));
    }
}
