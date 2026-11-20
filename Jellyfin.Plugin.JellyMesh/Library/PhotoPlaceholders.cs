using System;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The 1x1 images written as photo placeholders, with no EXIF and nothing of the remote photo (D7, D8).
/// </summary>
/// <remarks>
/// A format with no small image here, such as HEIC or a raw format, gets the JPEG under its own extension.
/// </remarks>
public static class PhotoPlaceholders
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg==";

    private const string Gif = "R0lGODlhAQABAIAAAP///wAAACwAAAAAAQABAAACAkQBADs=";

    private const string Bmp = "Qk06AAAAAAAAADYAAAAoAAAAAQAAAAEAAAABABgAAAAAAAQAAAATCwAAEwsAAAAAAAAAAAAAgICAAA==";

    private const string Webp = "UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA==";

    // An uncompressed 8-bit grey TIFF with only the tags a reader needs.
    private const string Tiff =
        "SUkqAAgAAAAJAAABAwABAAAAAQAAAAEBAwABAAAAAQAAAAIBAwABAAAACAAAAAMBAwABAAAAAQAAAAYBAwABAAAAAQAAABEBBAABAAAAegAAABUBAwAB" +
        "AAAAAQAAABYBAwABAAAAAQAAABcBBAABAAAAAQAAAAAAAACA";

    /// <summary>Returns the placeholder of a photo format.</summary>
    /// <param name="extension">The extension, without the dot.</param>
    /// <returns>A new copy of the file.</returns>
    public static byte[] For(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);

        return Convert.FromBase64String(extension.ToLowerInvariant() switch
        {
            "png" => Png,
            "gif" => Gif,
            "bmp" => Bmp,
            "webp" => Webp,
            "tif" or "tiff" => Tiff,
            _ => BookPlaceholders.PageJpeg
        });
    }
}
