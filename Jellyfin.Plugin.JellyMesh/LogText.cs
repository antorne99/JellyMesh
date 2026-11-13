using System;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh;

/// <summary>
/// Makes names read from remote servers safe to write to the log.
/// </summary>
public static class LogText
{
    /// <summary>
    /// The most characters of a name written to the log, before the ellipsis.
    /// </summary>
    public const int MaxNameLength = 200;

    /// <summary>
    /// Removes the characters <see cref="CleanMessage"/> removes, then cuts the name
    /// at <see cref="MaxNameLength"/> characters with an ellipsis.
    /// </summary>
    /// <param name="value">The name to log.</param>
    /// <returns>The safe name, the same string when it needs no change.</returns>
    public static string? Clean(string? value)
    {
        var cleaned = CleanMessage(value);
        if (cleaned is null || cleaned.Length <= MaxNameLength)
        {
            return cleaned;
        }

        var length = char.IsHighSurrogate(cleaned[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength;
        return string.Concat(cleaned.AsSpan(0, length), "…");
    }

    /// <summary>
    /// Removes control characters, line separators and bidi or zero-width characters,
    /// so that a text cannot forge log lines or change how they look. It is not cut.
    /// </summary>
    /// <param name="value">The text to log.</param>
    /// <returns>The text without those characters, the same string when it has none.</returns>
    public static string? CleanMessage(string? value)
    {
        if (value is null || !value.Any(IsUnsafe))
        {
            return value;
        }

        return string.Concat(value.Where(character => !IsUnsafe(character)));
    }

    private static bool IsUnsafe(char character)
    {
        return char.IsControl(character)
            || character is '\u2028' or '\u2029' or '\uFEFF'
            or (>= '\u200B' and <= '\u200F')
            or (>= '\u202A' and <= '\u202E')
            or (>= '\u2066' and <= '\u2069');
    }
}
