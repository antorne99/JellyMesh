using System;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The local thumbnail of a remote photo, the Primary of its placeholder (row 484): a re-encoded JPEG, never with EXIF.
/// </summary>
public static class PhotoThumbnails
{
    private const byte Marker = 0xFF;
    private const byte StartOfImage = 0xD8;
    private const byte EndOfImage = 0xD9;
    private const byte StartOfScan = 0xDA;
    private const byte App1 = 0xE1;

    /// <summary>
    /// Tells whether a file is a whole JPEG with no APP1 segment, the one that carries EXIF, GPS included, and XMP (row 470).
    /// </summary>
    /// <param name="bytes">The file.</param>
    /// <returns>True for such a JPEG; false for one with an APP1, a truncated one or any other format.</returns>
    public static bool IsCleanJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != Marker || bytes[1] != StartOfImage)
        {
            return false;
        }

        var inScan = false;
        var scanned = false;
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
                return scanned && i + 2 == bytes.Length;
            }

            if (marker == App1 || marker == 0x00 || i + 3 >= bytes.Length)
            {
                return false;
            }

            var length = (bytes[i + 2] << 8) | bytes[i + 3];
            if (length < 2 || i + 2 + length > bytes.Length)
            {
                return false;
            }

            inScan = marker == StartOfScan;
            scanned |= inScan;
            i += 2 + length;
        }

        return false;
    }
}
