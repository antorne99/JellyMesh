using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

/// <summary>The English of the page is the key of every translation, so each text has to be found in each language.</summary>
public class PageTranslationsTests
{
    private static readonly string _page = ReadPage();
    private static readonly string _script = _page[_page.IndexOf("(function () {", StringComparison.Ordinal)..];
    private static readonly string _markup = Between(_page, "<div data-role=\"content\">", "<script type=\"application/json\"");
    private static readonly Dictionary<string, Dictionary<string, string>> _languages = ReadLanguages();

    public static TheoryData<string> Languages => new(_languages.Keys);

    private static string ReadPage()
    {
        var name = typeof(FederationAdminController).Namespace + ".configPage.html";
        using var stream = typeof(FederationAdminController).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        return text[from..text.IndexOf(end, from, StringComparison.Ordinal)];
    }

    private static Dictionary<string, Dictionary<string, string>> ReadLanguages()
    {
        var block = Between(_page, "id=\"jmTranslations\">", "</script>");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(block["id=\"jmTranslations\">".Length..])!;
    }

    private static string Literal(string text) => text.Replace("\\'", "'", StringComparison.Ordinal);

    private static IEnumerable<string> ScriptTexts()
    {
        foreach (Match match in Regex.Matches(_script, @"\bt\('((?:[^'\\]|\\.)*)'"))
        {
            yield return Literal(match.Groups[1].Value);
        }

        foreach (Match match in Regex.Matches(_script, @"\bcount\([^,]+, '((?:[^'\\]|\\.)*)', '((?:[^'\\]|\\.)*)'\)"))
        {
            yield return Literal(match.Groups[1].Value);
            yield return Literal(match.Groups[2].Value);
        }
    }

    private static IEnumerable<string> MarkupTexts()
    {
        var markup = Regex.Replace(_markup, "<style.*?</style>", string.Empty, RegexOptions.Singleline);
        foreach (Match match in Regex.Matches(markup, ">([^<>]+)(?=<)"))
        {
            var text = string.Join(' ', match.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length > 0 && text != "JellyMesh")
            {
                yield return text;
            }
        }

        foreach (Match match in Regex.Matches(markup, "aria-label=\"([^\"]+)\""))
        {
            yield return match.Groups[1].Value;
        }
    }

    private static IEnumerable<string> ServerTexts()
    {
        foreach (var failure in Enum.GetValues<RemoteFailure>())
        {
            yield return FederationAdminController.FailureMessage(failure);
        }

        foreach (var state in Enum.GetValues<KeyState>().Where(state => state != KeyState.Ok))
        {
            yield return FederationAdminController.KeyWarning(state)!;
        }

        foreach (var outcome in Enum.GetValues<AutomaticLibrariesOutcome>())
        {
            yield return FederationAdminController.RefusalMessage(outcome);
        }

        foreach (var outcome in Enum.GetValues<ServerRenameOutcome>().Where(outcome => outcome != ServerRenameOutcome.Started))
        {
            yield return FederationAdminController.RenameRefusalMessage(outcome);
        }

        yield return FederationAdminController.ReleasedLibraryNotListed;
        yield return "A sync is already running.";
        yield return "This merge no longer exists.";
        yield return "Select at least two titles of one group.";
        yield return "Two local films cannot be merged: JellyMesh does not change local files. Select at most one title with a local copy.";
        yield return "Two local series cannot be merged: JellyMesh does not change local files. Select at most one series with a local copy.";
        yield return "The list changed. Check it again, then merge.";
    }

    private static string[] Holes(string text) => Regex.Matches(text, @"\{\w+\}").Select(match => match.Value).Distinct().Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void There_is_a_language_to_translate_to()
    {
        Assert.Contains("it", _languages.Keys);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_text_of_the_script_is_translated(string language)
    {
        Assert.Empty(ScriptTexts().Where(text => !_languages[language].ContainsKey(text)).Distinct());
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_text_of_the_markup_is_translated(string language)
    {
        Assert.Empty(MarkupTexts().Where(text => !_languages[language].ContainsKey(text)).Distinct());
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_text_from_the_server_is_translated(string language)
    {
        Assert.Empty(ServerTexts().Where(text => !_languages[language].ContainsKey(text)).Distinct());
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void A_language_holds_no_text_the_page_does_not_use(string language)
    {
        var used = ScriptTexts().Concat(MarkupTexts()).Concat(ServerTexts()).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(_languages[language].Keys, text => !used.Contains(text));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void A_translation_fills_the_same_values(string language)
    {
        Assert.Empty(_languages[language].Where(pair => !Holes(pair.Key).SequenceEqual(Holes(pair.Value))).Select(pair => pair.Key));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void A_translation_shows_no_context_mark(string language)
    {
        Assert.Empty(_languages[language].Where(pair => Regex.IsMatch(pair.Value, @"\|\w+$")).Select(pair => pair.Key));
    }

    [Fact]
    public void The_merge_date_of_series_has_its_own_text()
    {
        Assert.Contains("mergedOn: t('Merged on {date}|series')", _script, StringComparison.Ordinal);
        Assert.Equal("Unite il {date}", _languages["it"]["Merged on {date}|series"]);
        Assert.Equal("Uniti il {date}", _languages["it"]["Merged on {date}"]);
    }

    [Theory]
    [InlineData("without this backup")]
    [InlineData("senza questa riserva")]
    [InlineData("or add those servers again")]
    [InlineData("o aggiungi di nuovo quei server")]
    public void No_text_speaks_of_the_flat_layout_or_of_adding_missing_servers_again(string text)
    {
        Assert.DoesNotContain(text, _page, StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_a_server_says_its_folders_go_and_how_they_come_back()
    {
        Assert.Contains("t('The folders of this server are deleted now: its titles leave the libraries, with their images.", _script, StringComparison.Ordinal);
        Assert.Contains("t('Adding the server back, or restoring the settings, brings its folders back, with the same titles and watch state.')", _script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"create\('[a-z0-9]+', '[A-Z]")]
    [InlineData(@"\bbutton\('")]
    [InlineData(@"addFact\(\w+, '")]
    [InlineData(@"show(Message|Error)\('[A-Z]")]
    [InlineData(@"(text|label|name): '[A-Z]")]
    public void No_shown_text_skips_the_translation(string pattern)
    {
        Assert.Empty(Regex.Matches(_script, pattern).Select(match => match.Value));
    }
}
