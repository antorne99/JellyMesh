using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyMesh;

/// <summary>
/// Makes the whole text of an error safe for the log and the configuration page: no token, no password and no forged line.
/// </summary>
public static partial class ErrorText
{
    /// <summary>
    /// The most characters kept of one text, such as the body of an answer or an exception with its stack trace.
    /// </summary>
    public const int MaxLength = 64 * 1024;

    /// <summary>
    /// What stands in place of a token or a password.
    /// </summary>
    public const string Removed = "[removed]";

    /// <summary>
    /// Shortest secret that can be removed by value.
    /// </summary>
    internal const int MinSecretLength = 4;

    private const string LineStart = "\n    ";

    /// <summary>
    /// Writes an exception with its inner exceptions and stack traces, on lines of their own under the log line.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The safe text, starting with a line break.</returns>
    public static string Of(Exception? exception) => exception is null ? string.Empty : Block(Clean(exception.ToString(), []));

    /// <summary>
    /// Removes secrets and unsafe characters from a text and cuts it. Line breaks stay.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="secrets">The secrets known by value.</param>
    /// <returns>The safe text.</returns>
    internal static string Clean(string text, IReadOnlyCollection<string> secrets)
    {
        text = WithoutUnsafe(text);
        foreach (var form in secrets.Where(secret => secret.Length > 0).SelectMany(FormsOf).Distinct(StringComparer.Ordinal).OrderByDescending(form => form.Length))
        {
            if (!text.Contains(form, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (form.Length < MinSecretLength)
            {
                return "[not shown: it may hold a password too short to be removed]";
            }

            text = text.Replace(form, Removed, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            text = NamedSecret().Replace(text, match => match.Groups["name"].Value + Removed);
            text = AuthorizationScheme().Replace(text, match => match.Groups["name"].Value + Removed);
            text = UserInfo().Replace(text, "://" + Removed + "@");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[not shown: its secrets could not be removed in time]";
        }

        return text.Length <= MaxLength ? text : Cut(text);
    }

    /// <summary>
    /// Indents every line of a text, so that none of them can pass for a line of the log.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The indented text, starting with a line break.</returns>
    internal static string Block(string text) => LineStart + text.Replace("\n", LineStart, StringComparison.Ordinal);

    private static string Cut(string text)
    {
        var length = char.IsHighSurrogate(text[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
        return string.Concat(text.AsSpan(0, length), string.Create(System.Globalization.CultureInfo.InvariantCulture, $"\n[cut at {MaxLength} characters]"));
    }

    private static string WithoutUnsafe(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character == '\t')
            {
                builder.Append("    ");
            }
            else if (character == '\n' || !LogText.IsUnsafe(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    // The ways a secret can be written in an answer: as it is, in JSON, in a URL or in HTML.
    private static IEnumerable<string> FormsOf(string secret)
    {
        yield return secret;
        yield return JsonEncodedText.Encode(secret).ToString();
        yield return JsonEncodedText.Encode(secret, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();
        yield return Uri.EscapeDataString(secret);
        yield return WebUtility.UrlEncode(secret);
        yield return WebUtility.HtmlEncode(secret);
    }

    [GeneratedRegex(
        """(?<name>(?:token|api_?key|(?<![A-Za-z])pw|passw(?:or)?d|[?&]sig)(?:\\*["']|\\+u0022|&quot;|%22)?\s*(?:[:=]|%3[AD])\s*)(?:(?<quote>\\*["']|\\+u0022|&quot;|%22).*?(?:(?<!\\)\k<quote>|$)|[^\s&,;"'<>}\])]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex NamedSecret();

    [GeneratedRegex("""(?<name>\b(?:Bearer|Basic)\s+)[A-Za-z0-9._~+/=-]{8,}""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AuthorizationScheme();

    [GeneratedRegex("""://[^/\s@"'<>]{1,512}@""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex UserInfo();
}
