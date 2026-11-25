using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.JellyMesh.Index;

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

    private static readonly FrozenDictionary<string, string> _mediaTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".heic"] = "image/heic",
        [".heif"] = "image/heif",
        [".avif"] = "image/avif"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tells whether an entry of the index is a photo that gets a placeholder: one in a format with an extension.</summary>
    /// <param name="item">The entry.</param>
    /// <returns>Whether it gets one.</returns>
    public static bool HasPlaceholder(IndexedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.Kind == IndexedItemKind.Photo && item.Extension is not null;
    }

    /// <summary>Gives the media type a photo goes with, from the extension of its placeholder.</summary>
    /// <param name="path">The path of the placeholder.</param>
    /// <returns>The type of its format, application/octet-stream for a raw or any other format.</returns>
    public static string MediaTypeOf(string path)
        => _mediaTypes.TryGetValue(Path.GetExtension(path), out var type) ? type : "application/octet-stream";

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
