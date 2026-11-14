using System;
using System.Collections.Generic;
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
    public void AStubOfSeveralEpisodesNamesItsRange()
    {
        var path = FederatedPaths.EpisodeStub(FederatedPaths.SeriesFolderOf("Show (2020)"), "Show", 1, 2, 3);

        Assert.Equal(Path.Combine("Serie", "Show (2020)", "Season 01", "Show S01E02-E03.strm"), path);
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

    private static readonly Guid _other = Guid.Parse("9f8e7d6c-0000-0000-0000-000000000002");
    private static readonly Guid _third = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid _server = Guid.Parse("1a2b3c4d-0000-0000-0000-000000000001");

    [Fact]
    public void EachServerHasItsFolderNamedByItsGuid()
    {
        Assert.Equal(Path.Combine("/federata", "1a2b3c4d-0000-0000-0000-000000000001", "Film"), FederatedFolderLayout.ServerMoviesPath("/federata", _server));
        Assert.Equal(Path.Combine("/federata", "1a2b3c4d-0000-0000-0000-000000000001", "Serie"), FederatedFolderLayout.ServerSeriesPath("/federata", _server));
    }

    [Fact]
    public void AFilmStubOfAServerCarriesTheLabel()
    {
        var path = FederatedPaths.MovieStub(_server, "Film (2020)", "Casa");

        Assert.Equal(Path.Combine("1a2b3c4d-0000-0000-0000-000000000001", "Film", "Film (2020)", "Film (2020) - Casa.strm"), path);
    }

    [Fact]
    public void AnEpisodeStubOfAServerCarriesTheLabelAfterItsRange()
    {
        var series = FederatedPaths.SeriesFolderOf(_server, "Show (2020)");

        Assert.Equal(
            Path.Combine("1a2b3c4d-0000-0000-0000-000000000001", "Serie", "Show (2020)", "Season 01", "Show S01E02 - Casa.strm"),
            FederatedPaths.EpisodeStub(series, "Show", 1, 2, label: "Casa"));
        Assert.Equal(
            Path.Combine("1a2b3c4d-0000-0000-0000-000000000001", "Serie", "Show (2020)", "Season 01", "Show S01E02-E03 - Casa.strm"),
            FederatedPaths.EpisodeStub(series, "Show", 1, 2, 3, "Casa"));
    }

    [Fact]
    public void AServerStubResolvesInsideTheFolder()
    {
        var path = FederatedPaths.Resolve("/federata", FederatedPaths.MovieStub(_server, FederatedPaths.TitleFolder("../../x", 2020), "Casa"));

        Assert.StartsWith(Path.Combine("/federata", "1a2b3c4d-0000-0000-0000-000000000001", "Film") + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Casa", "Casa")]
    [InlineData("  Ca/sa  di\\Marco ", "Ca sa di Marco")]
    [InlineData("Casa-di_Marco.it", "Casa di Marco it")]
    [InlineData("Casa Trailer", "Casa")]
    [InlineData("Casa-trailers", "Casa")]
    [InlineData("Sample Casa", "Casa")]
    [InlineData("Casa 3D", "Casa")]
    [InlineData("Casa.HSBS mvc tab", "Casa")]
    public void ALabelIsTheServerNameWithoutExtrasSuffixesOr3DWords(string name, string expected)
    {
        Assert.Equal(expected, Label(name));
    }

    [Fact]
    public void ALabelWithNothingLeftIsTheStartOfTheGuid()
    {
        Assert.Equal("1a2b3c4d", Label("trailer 3d"));
    }

    [Fact]
    public void ALongLabelIsCut()
    {
        Assert.True(Label(new string('x', 500)).Length <= 40);
    }

    [Fact]
    public void OnTheSameNameOnlyTheServerThatComesLaterGetsItsGuid()
    {
        var labels = FederatedPaths.Labels([(_server, "Casa"), (_other, "casa trailer"), (_third, "Ufficio")], new Dictionary<Guid, string>());

        Assert.Equal("Casa", labels[_server]);
        Assert.Equal("casa 9f8e7d6c", labels[_other]);
        Assert.Equal("Ufficio", labels[_third]);
    }

    [Fact]
    public void AStoredLabelStaysAndANewServerWithTheSameNameGetsItsGuid()
    {
        var stored = new Dictionary<Guid, string> { [_server] = "Casa", [_third] = "Vecchio" };

        var labels = FederatedPaths.Labels([(_other, "Casa"), (_server, "Casa"), (_third, "Nuovo")], stored);

        Assert.Equal("Casa", labels[_server]);
        Assert.Equal("Casa 9f8e7d6c", labels[_other]);
        Assert.Equal("Vecchio", labels[_third]);
    }

    [Fact]
    public void ARenamedServerTakesTheLabelOfItsNewName()
    {
        var stored = new Dictionary<Guid, string> { [_server] = "Casa", [_other] = "Ufficio" };

        Assert.Equal("Mare", FederatedPaths.Relabel(_server, "Mare trailer", stored));
    }

    [Fact]
    public void ARenamedServerTakesItsGuidWhenAnotherLabelHoldsTheNewName()
    {
        var stored = new Dictionary<Guid, string> { [_server] = "Casa", [_other] = "Ufficio" };

        Assert.Equal("ufficio 1a2b3c4d", FederatedPaths.Relabel(_server, "ufficio", stored));
    }

    [Fact]
    public void ARenamedServerCanTakeBackTheLabelItHolds()
    {
        var stored = new Dictionary<Guid, string> { [_server] = "Casa 1a2b3c4d", [_other] = "Casa" };

        Assert.Equal("Casa 1a2b3c4d", FederatedPaths.Relabel(_server, "Casa", stored));
        Assert.Equal("Mare", FederatedPaths.Relabel(_server, "Mare", stored));
    }

    [Fact]
    public void ARenameThatChangesOnlyTheCaseKeepsTheLabel()
    {
        var stored = new Dictionary<Guid, string> { [_server] = "casa" };

        Assert.Equal("casa", FederatedPaths.Relabel(_server, "CASA", stored));
    }

    private static string Label(string name) => FederatedPaths.Labels([(_server, name)], new Dictionary<Guid, string>())[_server];
}
