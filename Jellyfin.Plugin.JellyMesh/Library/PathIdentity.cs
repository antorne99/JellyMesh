using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Compares paths by the folder they name on disk rather than by their text: symbolic links are followed and names are
/// compared in Unicode NFC. Bind mounts cannot be seen from the process.
/// </summary>
public static class PathIdentity
{
    // As many links as Linux follows in one lookup before it gives up with ELOOP.
    private const int MaxLinkHops = 40;

    private static readonly char[] _separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// Gets how two canonical paths compare on this system: without case on Windows and macOS, whose default
    /// filesystems ignore it, and by exact case elsewhere, Docker on macOS included.
    /// </summary>
    public static StringComparison Comparison { get; } = ComparisonFor(CurrentPlatform());

    /// <summary>
    /// Resolves the symbolic links in every component of a path and composes its names in NFC. What does not exist, or
    /// cannot be read, stays as written, and so does the whole path if its links loop.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The canonical path, without a trailing separator.</returns>
    public static string Canonical(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path);
        var resolved = Resolve(full) ?? Path.GetFullPath(full);
        return Path.TrimEndingDirectorySeparator(resolved).Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Tells whether a canonical path is a canonical folder or anywhere below it.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="folder">The folder.</param>
    /// <returns>Whether the path is within the folder.</returns>
    public static bool IsWithin(string path, string folder) => IsWithin(path, folder, Comparison);

    internal static bool IsWithin(string path, string folder, StringComparison comparison) =>
        string.Equals(path, folder, comparison)
        || path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar, comparison);

    internal static StringComparison ComparisonFor(OSPlatform platform) =>
        platform == OSPlatform.Windows || platform == OSPlatform.OSX ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static OSPlatform CurrentPlatform() =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;

    // Walks a full path one name at a time, following links as the kernel does. Null if the links loop.
    private static string? Resolve(string full)
    {
        var resolved = Path.GetPathRoot(full) ?? string.Empty;
        var pending = new Stack<string>();
        Push(pending, full[resolved.Length..]);
        var hops = 0;

        while (pending.TryPop(out var name))
        {
            if (name is "" or ".")
            {
                continue;
            }

            if (name == "..")
            {
                resolved = Path.GetDirectoryName(resolved) ?? resolved;
                continue;
            }

            var next = Path.Join(resolved, name);
            if (!TryReadLink(next, out var target))
            {
                // Nothing below a missing name can be a link, so the rest stays as written.
                return Path.GetFullPath(Path.Join([next, .. pending]));
            }

            if (target is null)
            {
                resolved = next;
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                return null;
            }

            // An absolute target starts again from its root, a relative one from the folder the link is in.
            var root = Path.GetPathRoot(target);
            if (!string.IsNullOrEmpty(root))
            {
                resolved = root;
            }

            Push(pending, target[(root?.Length ?? 0)..]);
        }

        return resolved;
    }

    // Puts the names of a relative path on the stack so that the first one comes out first.
    private static void Push(Stack<string> pending, string relative)
    {
        var names = relative.Split(_separators);
        for (var i = names.Length - 1; i >= 0; i--)
        {
            pending.Push(names[i]);
        }
    }

    // Reads where a link points, null for what is not a link. False when the name is missing or cannot be read.
    private static bool TryReadLink(string path, out string? target)
    {
        try
        {
            target = new FileInfo(path).LinkTarget;
            return target is not null || Path.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            target = null;
            return false;
        }
    }
}
