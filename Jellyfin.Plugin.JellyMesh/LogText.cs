using System.Linq;

namespace Jellyfin.Plugin.JellyMesh;

/// <summary>
/// Makes names read from remote servers safe to write to the log.
/// </summary>
public static class LogText
{
    /// <summary>
    /// Removes control characters, line separators and bidi or zero-width characters,
    /// so that a name cannot forge log lines or change how they look.
    /// </summary>
    /// <param name="value">The name to log.</param>
    /// <returns>The name without those characters, the same string when it has none.</returns>
    public static string? Clean(string? value)
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
