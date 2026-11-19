using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The minimal valid files written as placeholders for ebooks, PDFs and comics (decision D47, plan §10 row 486).
/// </summary>
/// <remarks>
/// They hold no remote data: the metadata goes on the locked item. Each is fixed, so every call returns the same bytes.
/// </remarks>
public static class BookPlaceholders
{
    private const ushort ZipVersion = 10;
    private const ushort ZipDate = 0x21; // 1 January 1980, the first DOS date.

    // The 1x1 grey JPEG (Pillow, quality 50) of the comic placeholder the F48 probes tested.
    private const string PageJpeg =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDABALDA4MChAODQ4SERATGCgaGBYWGDEjJR0oOjM9PDkzODdASFxOQERXRTc4UG1RV19i" +
        "Z2hnPk1xeXBkeFxlZ2P/2wBDARESEhgVGC8aGi9jQjhCY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2Nj" +
        "Y2NjY2NjY2P/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUF" +
        "BAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVW" +
        "V1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi" +
        "4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAEC" +
        "AxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVm" +
        "Z2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq" +
        "8vP09fb3+Pn6/9oADAMBAAIRAxEAPwAooooA/9k=";

    private const string EpubContainer =
        "<?xml version=\"1.0\"?>\n<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">"
        + "<rootfiles><rootfile full-path=\"content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>\n";

    private const string EpubPackage =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\">"
        + "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"></metadata><manifest/><spine/></package>\n";

    private static readonly string[] _pdfObjects =
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1 1] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1 1] >>"
    ];

    /// <summary>Returns a PDF with two empty 1x1 pages, the file the F48 probes tested.</summary>
    /// <returns>A new copy of the file.</returns>
    public static byte[] Pdf()
    {
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[_pdfObjects.Length];
        for (var i = 0; i < _pdfObjects.Length; i++)
        {
            offsets[i] = pdf.Length;
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{_pdfObjects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {_pdfObjects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {_pdfObjects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>Returns a CBZ with two 1x1 JPEG pages, like the file the F48 probes tested.</summary>
    /// <returns>A new copy of the file.</returns>
    public static byte[] Cbz()
    {
        var page = Convert.FromBase64String(PageJpeg);
        return Zip(("p1.jpg", page), ("p2.jpg", page));
    }

    /// <summary>
    /// Returns an EPUB whose package has no metadata, like the file the F48 probes tested. The mimetype entry comes first,
    /// stored, as EPUB readers expect.
    /// </summary>
    /// <returns>A new copy of the file.</returns>
    public static byte[] Epub()
    {
        return Zip(
            ("mimetype", Encoding.ASCII.GetBytes("application/epub+zip")),
            ("META-INF/container.xml", Encoding.UTF8.GetBytes(EpubContainer)),
            ("content.opf", Encoding.UTF8.GetBytes(EpubPackage)));
    }

    // Writes a zip with every entry stored and dated 1 January 1980, so that its bytes never change.
    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            var offsets = new int[entries.Length];
            for (var i = 0; i < entries.Length; i++)
            {
                offsets[i] = (int)stream.Position;
                WriteZipHeader(writer, entries[i].Name, entries[i].Data, null);
                writer.Write(entries[i].Data);
            }

            var directory = (int)stream.Position;
            for (var i = 0; i < entries.Length; i++)
            {
                WriteZipHeader(writer, entries[i].Name, entries[i].Data, offsets[i]);
            }

            var directorySize = (int)stream.Position - directory;
            writer.Write(0x06054b50u);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)entries.Length);
            writer.Write((ushort)entries.Length);
            writer.Write(directorySize);
            writer.Write(directory);
            writer.Write((ushort)0);
        }

        return stream.ToArray();
    }

    // Writes a local header, or a central directory header when the offset of the local one is given.
    private static void WriteZipHeader(BinaryWriter writer, string name, byte[] data, int? localOffset)
    {
        writer.Write(localOffset is null ? 0x04034b50u : 0x02014b50u);
        if (localOffset is not null)
        {
            writer.Write(ZipVersion);
        }

        writer.Write(ZipVersion);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(ZipDate);
        writer.Write(Crc32(data));
        writer.Write(data.Length);
        writer.Write(data.Length);
        writer.Write((ushort)name.Length);
        writer.Write((ushort)0);
        if (localOffset is { } offset)
        {
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0u);
            writer.Write(offset);
        }

        writer.Write(Encoding.ASCII.GetBytes(name));
    }

    // Computes the CRC-32 that zip stores for each entry.
    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
