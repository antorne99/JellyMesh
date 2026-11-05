using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Names and paths inside the federated folder. Titles are untrusted text from a remote, so a name is built from a safe
/// subset and a final check refuses any path that leaves the folder.
/// </summary>
public static partial class FederatedPaths
{
    /// <summary>The hidden file kept in <c>Film/</c> and <c>Serie/</c>. Without it, Jellyfin skips an empty path and keeps its items.</summary>
    public const string HiddenFileName = ".jellymesh";

    private const int MaxNameLength = 120;
    private const string FallbackName = "Untitled";
    private const string MoviesFolder = "Film";
    private const string SeriesFolder = "Serie";

    private static readonly StringComparison _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Turns a title into a folder or file name.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <returns>The name, never empty.</returns>
    public static string Sanitize(string? title)
    {
        var builder = new StringBuilder(title?.Length ?? 0);
        foreach (var character in (title ?? string.Empty).Normalize(NormalizationForm.FormC))
        {
            builder.Append(IsUnsafe(character) ? ' ' : character);
        }

        var name = WhiteSpace().Replace(builder.ToString(), " ").Trim('.', ' ');
        if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength].TrimEnd('.', ' ');
        }

        return name.Length == 0 ? FallbackName : name;
    }

    /// <summary>
    /// Builds the folder name of a title. A film without a <c>.nfo</c> keeps the year in its name on the remote, so the
    /// year is not added twice.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <param name="year">The year.</param>
    /// <returns>The name.</returns>
    public static string TitleFolder(string? title, int? year)
    {
        var name = Sanitize(WithoutYear(title, year));
        return year is { } value ? string.Create(CultureInfo.InvariantCulture, $"{name} ({value})") : name;
    }

    /// <summary>
    /// Removes a trailing <c>(year)</c> from a title when it matches the year.
    /// </summary>
    /// <param name="title">The title as the remote has it.</param>
    /// <param name="year">The year to look for.</param>
    /// <returns>The title.</returns>
    public static string? WithoutYear(string? title, int? year)
    {
        if (title is null || year is null)
        {
            return title;
        }

        var suffix = string.Create(CultureInfo.InvariantCulture, $"({year})");
        var trimmed = title.TrimEnd();
        return trimmed.EndsWith(suffix, StringComparison.Ordinal) && trimmed.Length > suffix.Length
            ? trimmed[..^suffix.Length].TrimEnd()
            : title;
    }

    /// <summary>Builds the relative folder of a film.</summary>
    /// <param name="folderName">The folder name.</param>
    /// <returns>The relative path.</returns>
    public static string MovieFolder(string folderName) => Path.Combine(MoviesFolder, folderName);

    /// <summary>Builds the relative folder of a series.</summary>
    /// <param name="folderName">The folder name.</param>
    /// <returns>The relative path.</returns>
    public static string SeriesFolderOf(string folderName) => Path.Combine(SeriesFolder, folderName);

    /// <summary>Builds the relative path of a film stub.</summary>
    /// <param name="folderName">The folder name.</param>
    /// <returns>The relative path of the <c>.strm</c>.</returns>
    public static string MovieStub(string folderName) => Path.Combine(MovieFolder(folderName), folderName + ".strm");

    /// <summary>Builds the relative path of an episode stub.</summary>
    /// <param name="seriesFolder">The relative folder of the series.</param>
    /// <param name="seriesName">The series name, for the file name.</param>
    /// <param name="season">The season.</param>
    /// <param name="episode">The episode.</param>
    /// <param name="lastEpisode">The last episode of a stub that covers several.</param>
    /// <returns>The relative path of the <c>.strm</c>.</returns>
    public static string EpisodeStub(string seriesFolder, string seriesName, int season, int episode, int? lastEpisode = null)
    {
        var folder = string.Create(CultureInfo.InvariantCulture, $"Season {season:00}");
        var range = lastEpisode is { } last ? string.Create(CultureInfo.InvariantCulture, $"-E{last:00}") : string.Empty;
        var file = string.Create(CultureInfo.InvariantCulture, $"{Sanitize(seriesName)} S{season:00}E{episode:00}{range}.strm");
        return Path.Combine(seriesFolder, folder, file);
    }

    /// <summary>
    /// Makes a path relative to the federated folder absolute, refusing one that leaves it.
    /// </summary>
    /// <param name="root">The federated folder.</param>
    /// <param name="relativePath">The relative path.</param>
    /// <returns>The absolute path.</returns>
    /// <exception cref="InvalidOperationException">The path is outside the folder.</exception>
    public static string Resolve(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(relativePath);

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(folder, relativePath));
        if (!full.StartsWith(folder + Path.DirectorySeparatorChar, _pathComparison))
        {
            throw new InvalidOperationException("A stub path would leave the federated folder.");
        }

        return full;
    }

    /// <summary>
    /// Makes a path relative to the federated folder.
    /// </summary>
    /// <param name="root">The federated folder, empty if none is set.</param>
    /// <param name="path">The path.</param>
    /// <returns>The relative path, null if it is not inside the folder.</returns>
    public static string? RelativeTo(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);

        if (root.Length == 0)
        {
            return null;
        }

        var folder = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return path.StartsWith(folder, _pathComparison) ? path[folder.Length..] : null;
    }

    private static bool IsUnsafe(char character)
    {
        return char.IsControl(character)
            || char.IsSurrogate(character)
            || character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '[' or ']'
            || Path.GetInvalidFileNameChars().Contains(character);
    }

    [GeneratedRegex(@"\s+", RegexOptions.None, 1000)]
    private static partial Regex WhiteSpace();
}
