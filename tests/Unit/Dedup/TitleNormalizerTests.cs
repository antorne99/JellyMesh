using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

public class TitleNormalizerTests
{
    [Theory]
    [InlineData("Sonda Senza ID", 2026, "sonda senza id")]
    [InlineData("  Molti    Spazi  ", 2026, "molti spazi")]
    [InlineData("L'Éclisse Fantasma", 2026, "eclisse fantasma")]
    [InlineData("L’Éclisse Fantasma", 2026, "eclisse fantasma")]
    [InlineData("Eclisse Fantasma", 2026, "eclisse fantasma")]
    [InlineData("ÉCLISSE FANTASMÀ", 2026, "eclisse fantasma")]
    [InlineData("Così è la Vita: Parte Due!", 2026, "cosi e vita parte due")]
    [InlineData("Un'Altra Storia", 2026, "altra storia")]
    [InlineData("The Matrix", 1999, "matrix")]
    [InlineData("Matrix, The", 1999, "matrix")]
    [InlineData("Il Buono, il Brutto, il Cattivo", 1966, "buono brutto cattivo")]
    [InlineData("Spider-Man", 2002, "spider man")]
    public void Normalize_RemovesCaseAccentsArticlesAndPunctuation(string title, int year, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Normalize(title, year));
    }

    [Theory]
    [InlineData("Sonda Senza ID (2026)", 2026, "sonda senza id")]
    [InlineData("Sonda Senza ID(2026)", 2026, "sonda senza id")]
    [InlineData("Sonda Senza ID (2026)  ", 2026, "sonda senza id")]
    public void Normalize_RemovesTrailingYearEqualToProductionYear(string title, int year, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Normalize(title, year));
    }

    [Theory]
    [InlineData("Odissea (2001)", 1968, "odissea 2001")]
    [InlineData("Odissea (2001)", null, "odissea 2001")]
    [InlineData("(2001) Odissea", 2001, "2001 odissea")]
    [InlineData("Odissea 2001", 2001, "odissea 2001")]
    public void Normalize_KeepsAYearThatIsNotATrailingProductionYear(string title, int? year, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Normalize(title, year));
    }

    [Fact]
    public void Normalize_MakesTheStrippedAndCleanNamesEqual()
    {
        // Without a .nfo Name keeps the year, with one it does not: the same film must match either way.
        Assert.Equal(
            TitleNormalizer.Normalize("Tears of Steel", 2012),
            TitleNormalizer.Normalize("Tears of Steel (2012)", 2012));
    }

    [Theory]
    [InlineData("The", "the")]
    [InlineData("La La", "la la")]
    [InlineData("A", "a")]
    public void Normalize_KeepsArticlesWhenNothingElseIsLeft(string title, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Normalize(title, 2026));
    }

    [Theory]
    [InlineData("!!!", "!!!")]
    [InlineData("...", "...")]
    [InlineData("(2026)", "(2026)")]
    public void Normalize_KeepsTitlesWithoutLettersOrDigits(string title, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Normalize(title, 2026));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_ReturnsEmptyForBlankTitles(string? title)
    {
        Assert.Equal(string.Empty, TitleNormalizer.Normalize(title, 2026));
    }

    [Fact]
    public void Normalize_KeepsLettersOfOtherAlphabets()
    {
        Assert.Equal("千と千尋の神隠し", TitleNormalizer.Normalize("千と千尋の神隠し", 2001));
        Assert.Equal("сталкер", TitleNormalizer.Normalize("Сталкер", 1979));
    }
}
