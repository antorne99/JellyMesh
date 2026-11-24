using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// What stands in place of a text that holds a password.
    /// </summary>
    internal const string Hidden = "[not shown: it may hold a password or a token]";

    /// <summary>
    /// Shortest secret that can be removed by value.
    /// </summary>
    internal const int MinSecretLength = 4;

    /// <summary>
    /// Longest secret that is looked for by value.
    /// </summary>
    internal const int MaxSecretLength = 4 * 1024;

    /// <summary>
    /// The most lines kept of one text.
    /// </summary>
    internal const int MaxLines = 2000;

    // Cleaned past the cut, so that a secret across it is still whole when it is removed.
    private const int Margin = 8 * 1024;

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
    /// <param name="dropped">How many characters of the end to drop, where a text that broke off can hold part of a secret.</param>
    /// <returns>The safe text.</returns>
    internal static string Clean(string text, IReadOnlyCollection<string> secrets, int dropped = 0)
    {
        if (secrets.Any(secret => secret.Length > MaxSecretLength))
        {
            return Hidden;
        }

        text = WithoutUnsafe(text);
        if (text.Length > MaxLength + Margin)
        {
            text = text[..(MaxLength + Margin)];
            dropped = secrets.Select(LongestForm).Append(dropped).Max();
        }

        foreach (var form in secrets.SelectMany(FormsOf).Distinct(StringComparer.Ordinal).OrderByDescending(form => form.Length))
        {
            if (!text.Contains(form, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (form.Length < MinSecretLength)
            {
                return Hidden;
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

        return Cut(dropped == 0 ? text : text[..^Math.Min(dropped, text.Length)]);
    }

    /// <summary>
    /// Says whether a text holds a secret, in any way it can be written in an answer.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="secret">The secret.</param>
    /// <returns>Whether it does, or the secret is too long to tell.</returns>
    internal static bool Holds(string text, string secret)
    {
        if (secret.Length > MaxSecretLength)
        {
            return true;
        }

        text = WithoutUnsafe(text);
        return FormsOf(secret).Any(form => text.Contains(form, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the length of the longest way a secret can be written in an answer.
    /// </summary>
    /// <param name="secret">The secret.</param>
    /// <returns>The length.</returns>
    internal static int LongestForm(string secret) => secret.Length > MaxSecretLength ? MaxLength : FormsOf(secret).Select(form => form.Length).DefaultIfEmpty().Max();

    /// <summary>
    /// Indents every line of a text, so that none of them can pass for a line of the log.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The indented text, starting with a line break.</returns>
    internal static string Block(string text) => LineStart + text.Replace("\n", LineStart, StringComparison.Ordinal);

    private static string Cut(string text)
    {
        var end = 0;
        for (var line = 0; line < MaxLines && end >= 0; line++)
        {
            end = text.IndexOf('\n', line == 0 ? 0 : end + 1);
        }

        if (end >= 0 && end <= MaxLength)
        {
            return string.Concat(text.AsSpan(0, end), string.Create(CultureInfo.InvariantCulture, $"\n[cut at {MaxLines} lines]"));
        }

        if (text.Length <= MaxLength)
        {
            return text;
        }

        var length = char.IsHighSurrogate(text[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
        return string.Concat(text.AsSpan(0, length), string.Create(CultureInfo.InvariantCulture, $"\n[cut at {MaxLength} characters]"));
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

    // The ways a secret can be written in an answer, without the characters the text loses.
    private static IEnumerable<string> FormsOf(string secret) => EncodingsOf(secret).Select(WithoutUnsafe).Where(form => form.Length > 0);

    // A secret as it is, in JSON, in a URL, in HTML or in base64.
    private static IEnumerable<string> EncodingsOf(string secret)
    {
        secret = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(secret));
        yield return secret;
        foreach (var encoder in new[] { JavaScriptEncoder.Default, JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
        {
            var json = JsonEncodedText.Encode(secret, encoder).ToString();
            yield return json;
            yield return json.Replace("/", "\\/", StringComparison.Ordinal);
        }

        var url = Uri.EscapeDataString(secret);
        yield return url;
        yield return Uri.EscapeDataString(url);
        yield return WebUtility.UrlEncode(secret);
        yield return WebUtility.HtmlEncode(secret);
        yield return HtmlEncoder.Default.Encode(secret);
        yield return Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
    }

    [GeneratedRegex(
        """(?<name>(?:tokens?|api[_-]?key|(?<![A-Za-z])(?:new|current|old)?pw|pwd|passw(?:or)?d|(?:[?&]|&amp;|%26)sig)(?:\\*["']|\\+u0022|&quot;|&apos;|&#(?:34|39|x22|x27);|%22)?\]?\s*(?:[:=](?:\s*(?:>|&gt;))?|>|&gt;|%3[AD])\s*)(?:(?<quote>\\*["']|\\+u0022|&quot;|&apos;|&#(?:34|39|x22|x27);|%22).*?(?:(?<!\\)\k<quote>|$)|\[[^\]]*|\{[^}]*|[^\s&,;"'<>}\])]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex NamedSecret();

    [GeneratedRegex("""(?<name>\b(?:Bearer|Basic)\s+)[A-Za-z0-9._~+/=-]{8,}""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AuthorizationScheme();

    [GeneratedRegex(""":(?:\\?/){2}[^/\s"'<>]{1,512}@""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex UserInfo();
}
