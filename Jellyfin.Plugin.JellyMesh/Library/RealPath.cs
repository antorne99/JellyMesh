using System;
using System.IO;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Compares paths as the disk sees them: links resolved, case by the rules of the OS, so that an alias of the federated
/// folder is still the federated folder.
/// </summary>
public static class RealPath
{
    // A chain of links longer than this is taken as a loop.
    private const int MaxLinks = 32;

    private static readonly StringComparison _comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>Gives a path below a root in the spelling of the root, when its real path is inside the real root.</summary>
    /// <param name="root">The root.</param>
    /// <param name="path">The path.</param>
    /// <returns>The path under the root, null when it is outside or cannot be read.</returns>
    public static string? Rebase(string root, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var realRoot = Resolve(root)?.TrimEnd(Path.DirectorySeparatorChar);
        var realPath = Resolve(path);
        if (string.IsNullOrEmpty(realRoot) || realPath is null || !realPath.StartsWith(realRoot + Path.DirectorySeparatorChar, _comparison))
        {
            return null;
        }

        return Path.Join(root.TrimEnd(Path.DirectorySeparatorChar), realPath[realRoot.Length..]);
    }

    /// <summary>Resolves every link along a path; parts that do not exist are kept as written.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The real path, null when it cannot be read or the links loop.</returns>
    public static string? Resolve(string path)
    {
        try
        {
            var links = 0;
            return Resolve(Path.GetFullPath(path), ref links);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? Resolve(string full, ref int links)
    {
        var top = Path.GetPathRoot(full)!;
        var current = top;
        foreach (var part in full[top.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not { } target)
            {
                continue;
            }

            if (++links > MaxLinks)
            {
                return null;
            }

            var resolved = Resolve(Path.GetFullPath(target, Path.GetDirectoryName(current)!), ref links);
            if (resolved is null)
            {
                return null;
            }

            current = resolved;
        }

        return current;
    }
}
