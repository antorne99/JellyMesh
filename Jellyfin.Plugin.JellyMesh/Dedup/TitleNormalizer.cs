using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// Normalizes titles for the title + year match (plan §4.1).
/// </summary>
public static class TitleNormalizer
{
    // Italian and English. Elided forms (l', un') arrive here as "l" and "un", since an apostrophe separates words.
    private static readonly FrozenSet<string> _articles = FrozenSet.ToFrozenSet(
        ["il", "lo", "la", "i", "gli", "le", "l", "un", "uno", "una", "the", "a", "an"],
        StringComparer.Ordinal);

    /// <summary>
    /// Normalizes a title: removes a trailing <c>(year)</c> equal to <paramref name="year"/>, then lowercases and removes
    /// accents, punctuation and articles, compacting spaces.
    /// </summary>
    /// <remarks>
    /// A title made only of articles keeps them, and one with no letters or digits stays as it is apart from case,
    /// so that different titles do not collapse into an empty string.
    /// </remarks>
    /// <param name="title">The title.</param>
    /// <param name="year">The production year, or <c>null</c> when unknown.</param>
    /// <returns>The normalized title, empty only when <paramref name="title"/> is blank.</returns>
    public static string Normalize(string? title, int? year)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var trimmed = title.Trim();
        var words = SplitWords(RemoveTrailingYear(trimmed, year));
        if (words.Count == 0)
        {
            return trimmed.ToLowerInvariant();
        }

        var kept = words.Where(word => !_articles.Contains(word)).ToList();
        return string.Join(' ', kept.Count > 0 ? kept : words);
    }

    /// <summary>
    /// Splits a text into lowercase words without accents or punctuation. Unlike <see cref="Normalize"/> it keeps articles.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The words, empty when the text has no letters or digits.</returns>
    public static IReadOnlyList<string> Words(string? text) => string.IsNullOrWhiteSpace(text) ? [] : SplitWords(text);

    private static string RemoveTrailingYear(string title, int? year)
    {
        if (year is null)
        {
            return title;
        }

        var suffix = string.Create(CultureInfo.InvariantCulture, $"({year})");
        return title.EndsWith(suffix, StringComparison.Ordinal) ? title[..^suffix.Length] : title;
    }

    private static List<string> SplitWords(string title)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var character in title.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (char.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                word.Append(character);
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return words;
    }
}
