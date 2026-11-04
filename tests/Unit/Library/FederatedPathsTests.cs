using System;
using System.IO;
using Jellyfin.Plugin.JellyMesh.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class FederatedPathsTests
{
    [Theory]
    [InlineData("../../etc/passwd", "etc passwd")]
    [InlineData("..", "Untitled")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a b c d e f g h i j")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("trailing dots...", "trailing dots")]
    [InlineData(".hidden", "hidden")]
    [InlineData("name [tmdbid-999]", "name tmdbid-999")]
    [InlineData("line\nbreak\u0000", "line break")]
    [InlineData("", "Untitled")]
    [InlineData(null, "Untitled")]
    public void ANameCannotLeaveItsFolderOrHideOrCarryATag(string? title, string expected)
    {
        Assert.Equal(expected, FederatedPaths.Sanitize(title));
    }

    [Fact]
    public void AVeryLongTitleIsCut()
    {
        Assert.True(FederatedPaths.Sanitize(new string('x', 500)).Length <= 120);
    }

    [Theory]
    [InlineData("Sonda Senza ID (2026)", 2026, "Sonda Senza ID (2026)")]
    [InlineData("Sonda Senza ID", 2026, "Sonda Senza ID (2026)")]
    [InlineData("Blade Runner (1982)", 2017, "Blade Runner (1982) (2017)")]
    [InlineData("No Year", null, "No Year")]
    public void TheYearIsNotDoubled(string title, int? year, string expected)
    {
        Assert.Equal(expected, FederatedPaths.TitleFolder(title, year));
    }

    [Fact]
    public void AnEpisodeStubHoldsItsNumbers()
    {
        var path = FederatedPaths.EpisodeStub(FederatedPaths.SeriesFolderOf("Show (2020)"), "Show", 1, 2);

        Assert.Equal(Path.Combine("Serie", "Show (2020)", "Season 01", "Show S01E02.strm"), path);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("Film/../../outside")]
    [InlineData("/etc/passwd")]
    public void APathOutsideTheFolderIsRefused(string relative)
    {
        Assert.Throws<InvalidOperationException>(() => FederatedPaths.Resolve("/federata", relative));
    }

    [Fact]
    public void AFolderWithTheSamePrefixIsNotInside()
    {
        Assert.Throws<InvalidOperationException>(() => FederatedPaths.Resolve("/federata", "../federata-other/x"));
        Assert.Null(FederatedPaths.RelativeTo("/federata", "/federata-other/x.strm"));
    }

    [Fact]
    public void RelativeToFindsThePathInsideTheFolder()
    {
        Assert.Equal("Film/a.strm", FederatedPaths.RelativeTo("/federata/", "/federata/Film/a.strm"));
        Assert.Null(FederatedPaths.RelativeTo(string.Empty, "/federata/Film/a.strm"));
    }
}
