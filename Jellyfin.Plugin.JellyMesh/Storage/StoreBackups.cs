using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// Names, lists and prunes the nightly copies. Every name starts with the database's, so
/// the narrowing at open covers them.
/// </summary>
internal static class StoreBackups
{
    /// <summary>How many copies stay (row 427).</summary>
    public const int Kept = 2;

    private const string BackupMark = ".backup-";
    private const string TimeFormat = "yyyyMMddHHmmss";

    /// <summary>
    /// Gets the path of the copy made at a time.
    /// </summary>
    /// <param name="folder">The plugin data folder.</param>
    /// <param name="time">When the copy is made.</param>
    /// <returns>The path.</returns>
    public static string BackupPath(string folder, DateTimeOffset time) =>
        Path.Combine(folder, PluginDatabase.FileName + BackupMark + Stamp(time));

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
    /// Deletes every copy but the newest <see cref="Kept"/>.
    /// </summary>
    /// <param name="folder">The plugin data folder.</param>
    public static void Prune(string folder)
    {
        foreach (var copy in List(folder).Skip(Kept))
        {
            File.Delete(copy.FilePath);
        }
    }

    private static string Stamp(DateTimeOffset time) => time.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);
}
