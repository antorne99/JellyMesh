using System;
using System.IO;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The local thumbnail of a remote photo, the Primary of its placeholder (row 484): a re-encoded JPEG, never with EXIF.
/// </summary>
public static class PhotoThumbnails
{
    /// <summary>
    /// The folder of the thumbnails under the federated folder: hidden, so that Jellyfin skips it, and in no library.
    /// </summary>
    public const string FolderName = ".jellymesh-thumbs";

    private const byte Marker = 0xFF;
    private const byte StartOfImage = 0xD8;
    private const byte EndOfImage = 0xD9;
    private const byte StartOfScan = 0xDA;

    /// <summary>Gives the path of the thumbnail of a photo stub.</summary>
    /// <param name="root">The federated folder.</param>
    /// <param name="stubId">The stub.</param>
    /// <returns>The path, null for an id that is not a plain file name.</returns>
    public static string? PathOf(string root, string stubId)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(stubId);

        return stubId.Length == 0 || stubId.StartsWith('.') || stubId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || stubId.Contains('\\', StringComparison.Ordinal)
            ? null
            : Path.Combine(root, FolderName, stubId + ".jpg");
    }

    /// <summary>Tells whether a path is a file of a thumbnail folder, whatever the federated folder is.</summary>
    /// <param name="path">The path.</param>
    /// <returns>True for a thumbnail.</returns>
    public static bool IsThumbnail(string? path)
        => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), FolderName, StringComparison.Ordinal);

    /// <summary>
    /// Points the Primary of a photo at its thumbnail, as a new image so that Jellyfin measures it again. Nothing is saved.
    /// </summary>
    /// <param name="photo">The photo.</param>
    /// <param name="path">The thumbnail.</param>
    /// <returns>Whether the Primary changed; false when the thumbnail is not on disk.</returns>
    public static bool SetPrimary(BaseItem photo, string path)
    {
        ArgumentNullException.ThrowIfNull(photo);

        if (!File.Exists(path))
        {
            return false;
        }

        var modified = File.GetLastWriteTimeUtc(path);
        var current = photo.GetImageInfo(ImageType.Primary, 0);
        if (current is not null && string.Equals(current.Path, path, StringComparison.Ordinal) && current.DateModified == modified)
        {
            return false;
        }

        var image = new ItemImageInfo { Path = path, Type = ImageType.Primary, DateModified = modified };
        if (current is null)
        {
            photo.AddImage(image);
        }
        else
        {
            photo.SetImage(image, 0);
        }

        return true;
    }

    /// <summary>
    /// Tells whether a file is a whole JPEG with only the segments of the image, so no EXIF, GPS, XMP or IPTC (row 470),
    /// and no larger than the thumbnail asked of the remote.
    /// </summary>
    /// <param name="bytes">The file.</param>
    /// <returns>True for such a JPEG; false for one with other data, a truncated one or any other format.</returns>
    public static bool IsCleanJpeg(ReadOnlySpan<byte> bytes) => IsCleanJpeg(bytes, JellyfinRemoteClient.PhotoThumbnailSide);

    /// <summary>
    /// Tells whether a file is a whole JPEG with only the segments of the image, so no EXIF, GPS, XMP or IPTC (row 470),
    /// and no larger than a side.
    /// </summary>
    /// <param name="bytes">The file.</param>
    /// <param name="maxSide">The longest side allowed, in pixels.</param>
    /// <returns>True for such a JPEG; false for one with other data, a truncated one or any other format.</returns>
    public static bool IsCleanJpeg(ReadOnlySpan<byte> bytes, int maxSide)
    {
        if (bytes.Length < 4 || bytes[0] != Marker || bytes[1] != StartOfImage)
        {
            return false;
        }

        var inScan = false;
        var scanned = false;
        var framed = false;
        var i = 2;
        while (i < bytes.Length - 1)
        {
            if (bytes[i] != Marker)
            {
                // Outside the coded data of a scan every byte belongs to a segment.
                if (!inScan)
                {
                    return false;
                }

                i++;
                continue;
            }

            var marker = bytes[i + 1];
            if (marker == Marker || (inScan && (marker == 0x00 || marker is >= 0xD0 and <= 0xD7)))
            {
                // A fill byte, a stuffed data byte or a restart marker.
                i += marker == Marker ? 1 : 2;
                continue;
            }

            if (marker == EndOfImage)
            {
                return framed && scanned && i + 2 == bytes.Length;
            }

            if (!IsPlainImageSegment(marker) || i + 3 >= bytes.Length)
            {
                return false;
            }

            var length = (bytes[i + 2] << 8) | bytes[i + 3];
            if (length < 2 || i + 2 + length > bytes.Length)
            {
                return false;
            }

            if (!IsPlainApplicationData(marker, bytes.Slice(i + 4, length - 2)))
            {
                return false;
            }

            if (IsFrame(marker))
            {
                // Precision, then height and width: neither may be above the side asked of the remote.
                if (length < 7 || !Fits((bytes[i + 5] << 8) | bytes[i + 6], maxSide) || !Fits((bytes[i + 7] << 8) | bytes[i + 8], maxSide))
                {
                    return false;
                }

                framed = true;
            }

            inScan = marker == StartOfScan;
            scanned |= inScan;
            i += 2 + length;
        }

        return false;
    }

    // JFIF with no thumbnail, an ICC profile or the Adobe transform; a segment that is not application data passes.
    private static bool IsPlainApplicationData(byte marker, ReadOnlySpan<byte> payload) => marker switch
    {
        0xE0 => payload.Length == 14 && payload.StartsWith("JFIF\0"u8),
        0xE2 => payload.StartsWith("ICC_PROFILE\0"u8),
        0xEE => payload.Length == 12 && payload.StartsWith("Adobe"u8),
        _ => true
    };

    private static bool IsFrame(byte marker) => marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);

    private static bool Fits(int side, int maxSide) => side > 0 && side <= maxSide;

    // The segments an encoder writes for the image itself: JFIF, ICC profile, Adobe, tables, frames, restart interval
    // and scans. Markers with no length and any other application data or comment are refused.
    private static bool IsPlainImageSegment(byte marker)
        => marker is 0xE0 or 0xE2 or 0xEE or 0xDB or 0xC4 or 0xCC or 0xDD or StartOfScan
            || IsFrame(marker);
}
