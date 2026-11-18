using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// Names, lists and prunes the nightly copies, and names and lists the damaged files set aside. Every name starts with the
/// database's, so the narrowing at open covers them.
/// </summary>
internal static partial class StoreBackups
{
    /// <summary>How many copies stay (row 427).</summary>
    public const int Kept = 2;

    /// <summary>How long a damaged file set aside stays (D35).</summary>
    public static readonly TimeSpan DamagedKept = TimeSpan.FromDays(30);

    private const string BackupMark = ".backup-";
    private const string DamagedMark = ".damaged-";
    private const string PartialMark = ".partial";
    private const string TimeFormat = "yyyyMMddHHmmss";

    /// <summary>The file that keeps the last recovery for the page until the admin dismisses it.</summary>
    public const string RecoveryFileName = PluginDatabase.FileName + ".recovery";

    /// <summary>
    /// Gets the path of the copy made at a time.
    /// </summary>
    /// <param name="folder">The plugin data folder.</param>
    /// <param name="time">When the copy is made.</param>
    /// <returns>The path.</returns>
    public static string BackupPath(string folder, DateTimeOffset time) =>
        Path.Combine(folder, PluginDatabase.FileName + BackupMark + Stamp(time));

    /// <summary>
    /// Gets a free path to set the damaged database aside at a time, as <c>jellymesh.db.damaged-&lt;date&gt;</c>.
    /// </summary>
    /// <param name="folder">The plugin data folder.</param>
    /// <param name="time">When the file is set aside.</param>
    /// <returns>A path no file has yet.</returns>
    public static string DamagedPath(string folder, DateTimeOffset time)
    {
        var path = Path.Combine(folder, PluginDatabase.FileName + DamagedMark + Stamp(time));
        var candidate = path;
        for (var number = 2; File.Exists(candidate) || File.Exists(candidate + "-wal") || File.Exists(candidate + "-journal"); number++)
        {
            candidate = path + "-" + number.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    /// <summary>
    /// Gets the path a copy is written to until it is complete; listing never takes it for a copy.
    /// </summary>
    /// <param name="copyPath">The path of the copy.</param>
    /// <returns>The path.</returns>
    public static string PartialPath(string copyPath) => copyPath + PartialMark;

    /// <summary>
    /// Lists what copies cut short left, such as by a restart in the middle of one.
    /// </summary>
    /// <param name="folder">The plugin data folder.</param>
    /// <returns>The paths.</returns>
    public static IReadOnlyList<string> Partials(string folder) =>
        Directory.EnumerateFiles(folder, PluginDatabase.FileName + BackupMark + "*" + PartialMark).ToList();

    /// <summary>
    /// Lists the copies in the folder, the newest first.
    /// </summary>
    /// <param name="folder">The plugin data folder.</param>
    /// <returns>The copies.</returns>
    public static IReadOnlyList<StoreBackup> List(string folder)
    {
        var prefix = PluginDatabase.FileName + BackupMark;
        var copies = new List<StoreBackup>();
        foreach (var path in Directory.EnumerateFiles(folder, prefix + "*"))
        {
            var stamp = Path.GetFileName(path)[prefix.Length..];
            if (stamp.Length == TimeFormat.Length
                && DateTimeOffset.TryParseExact(stamp, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
            {
                copies.Add(new StoreBackup(path, time));
            }
        }

        return copies.OrderByDescending(copy => copy.CreatedAt).ToList();
    }

    /// <summary>
    /// Lists the copies in the order a recovery tries them: those dated at or before <paramref name="now"/> newest first,
    /// then those dated after it, which a clock running ahead made, nearest first, as a last resort before starting empty.
    /// </summary>
    /// <param name="folder">The plugin data folder.</param>
    /// <param name="now">When the recovery runs.</param>
    /// <returns>The copies, in the order to try them.</returns>
    public static IReadOnlyList<StoreBackup> ForRestore(string folder, DateTimeOffset now)
    {
        var copies = List(folder);
        return copies.Where(copy => copy.CreatedAt <= now)
            .Concat(copies.Where(copy => copy.CreatedAt > now).OrderBy(copy => copy.CreatedAt))
            .ToList();
    }

    /// <summary>
    /// Lists the copies to prune: the copy just written and the newest one dated before it stay, <see cref="Kept"/> in all; the
    /// others go, and so do those dated after it, which a clock running ahead made.
    /// </summary>
    /// <param name="folder">The plugin data folder.</param>
    /// <param name="written">The copy just written.</param>
    /// <returns>The paths of the copies that go.</returns>
    public static IReadOnlyList<string> Pruned(string folder, StoreBackup written)
    {
        var earlier = 0;
        var pruned = new List<string>();
        foreach (var copy in List(folder))
        {
            if (string.Equals(copy.FilePath, written.FilePath, StringComparison.Ordinal))
            {
                continue;
            }

            if (copy.CreatedAt < written.CreatedAt && ++earlier < Kept)
            {
                continue;
            }

            pruned.Add(copy.FilePath);
        }

        return pruned;
    }

    /// <summary>
    /// Lists the damaged files set aside and their logs, each with the date in its name; names without one are left out.
    /// </summary>
    /// <param name="folder">The plugin data folder.</param>
    /// <returns>The files with their dates.</returns>
    public static IReadOnlyList<StoreBackup> ListDamaged(string folder)
    {
        var files = new List<StoreBackup>();
        foreach (var path in Directory.EnumerateFiles(folder, PluginDatabase.FileName + DamagedMark + "*"))
        {
            var match = DamagedName().Match(Path.GetFileName(path));
            if (match.Success
                && DateTimeOffset.TryParseExact(match.Groups[1].Value, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
            {
                files.Add(new StoreBackup(path, time));
            }
        }

        return files;
    }

    /// <summary>
    /// Writes zeros over the whole file and flushes them to disk, then deletes it; a link is only deleted, never written
    /// through. A file that cannot be overwritten is not deleted.
    /// </summary>
    /// <param name="path">The file.</param>
    public static void Erase(string path)
    {
        if (new FileInfo(path).LinkTarget is null)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            var zeros = new byte[81920];
            for (var left = stream.Length; left > 0; left -= zeros.Length)
            {
                stream.Write(zeros, 0, (int)Math.Min(left, zeros.Length));
            }

            stream.Flush(true);
        }

        File.Delete(path);
    }

    [GeneratedRegex(@"^jellymesh\.db\.damaged-([0-9]{14})(?:-[0-9]+)?(?:-wal|-journal)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DamagedName();

    private static string Stamp(DateTimeOffset time) => time.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);
}
