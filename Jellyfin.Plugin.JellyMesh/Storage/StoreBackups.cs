using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// Names, lists and prunes the nightly copies, and names the damaged files set aside. Every name starts with the database's, so
/// the narrowing at open covers them.
/// </summary>
internal static class StoreBackups
{
    /// <summary>How many copies stay (row 427).</summary>
    public const int Kept = 2;

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
    /// Deletes what copies cut short left, such as by a restart in the middle of one.
    /// </summary>
    /// <param name="folder">The plugin data folder.</param>
    public static void DeletePartials(string folder)
    {
        foreach (var path in Directory.EnumerateFiles(folder, PluginDatabase.FileName + BackupMark + "*" + PartialMark))
        {
            File.Delete(path);
        }
    }

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
    /// Keeps the copy just written and the newest one dated before it, <see cref="Kept"/> in all; the others go, and so do
    /// those dated after it, which a clock running ahead made.
    /// </summary>
    /// <param name="folder">The plugin data folder.</param>
    /// <param name="written">The copy just written.</param>
    public static void Prune(string folder, StoreBackup written)
    {
        var earlier = 0;
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

            File.Delete(copy.FilePath);
        }
    }

    private static string Stamp(DateTimeOffset time) => time.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);
}
