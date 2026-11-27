using System;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Reads the URL of a <c>.strm</c> whose item carries none, as an audio item, wherever it lies (row 582).
/// </summary>
public static class StrmFile
{
    // The most a .strm of the plugin holds: its URL is about 140 bytes.
    private const int MaxBytes = 1024;

    /// <summary>
    /// Reads the text of a <c>.strm</c> of at most 1 KiB; a FIFO, a device or an empty file, also behind a link, has a length
    /// of 0 and is never opened.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The text, null for a file that is not a <c>.strm</c>, cannot be read, is empty or too long.</returns>
    public static string? ReadUrl(string? path)
    {
        if (path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        try
        {
            // A link is judged by the file it ends at, which is what an open reads.
            var file = new FileInfo(path);
            var info = file.LinkTarget is null ? file : file.ResolveLinkTarget(returnFinalTarget: true) as FileInfo;
            if (info is null || !info.Exists || info.Length is < 1 or > MaxBytes || (info.Attributes & (FileAttributes.Device | FileAttributes.Directory)) != 0)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            var buffer = new byte[MaxBytes + 1];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return read is > 0 and <= MaxBytes ? Encoding.UTF8.GetString(buffer, 0, read) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the stub named by the URL of a <c>.strm</c>, as <see cref="ReadUrl"/> reads it.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The stub identifier, null when the file holds no URL of the plugin.</returns>
    public static string? StubIdOf(string? path) => PlaybackRoutes.TryGetStubId(ReadUrl(path), out var stubId) ? stubId : null;
}
