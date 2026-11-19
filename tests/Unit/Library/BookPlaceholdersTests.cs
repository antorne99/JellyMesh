using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyMesh.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class BookPlaceholdersTests
{
    // The SHA-256 of the PDF that the F48 probes generated and Jellyfin 12.4 refreshed with no errors.
    private const string ProbePdfSha256 = "8557c76e0e2f4e3d4423ee6618ca408ca10b1626fd7303e116cddaca39b8a6af";

    [Fact]
    public void Pdf_IsTheFileTheF48ProbesTested()
    {
        var pdf = BookPlaceholders.Pdf();

        Assert.Equal(418, pdf.Length);
        Assert.Equal(ProbePdfSha256, Convert.ToHexStringLower(SHA256.HashData(pdf)));
    }

    [Fact]
    public void Pdf_ParsesWithTwoPages()
    {
        var text = Encoding.ASCII.GetString(BookPlaceholders.Pdf());
        Assert.StartsWith("%PDF-1.4\n", text, StringComparison.Ordinal);

        var objects = PdfObjects(text, out var trailer);
        var root = Reference(trailer, "Root");
        Assert.Contains("/Type /Catalog", objects[root], StringComparison.Ordinal);

        var pages = Reference(objects[root], "Pages");
        Assert.Contains("/Type /Pages", objects[pages], StringComparison.Ordinal);
        var kids = Regex.Matches(Regex.Match(objects[pages], @"/Kids \[([^\]]*)\]").Groups[1].Value, @"(\d+) 0 R")
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();
        Assert.Equal(2, int.Parse(Regex.Match(objects[pages], @"/Count (\d+)").Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.Equal(2, kids.Count);
        Assert.All(kids, kid =>
        {
            Assert.Matches(@"/Type /Page\b(?!s)", objects[kid]);
            Assert.Equal(pages, Reference(objects[kid], "Parent"));
        });
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("cbz")]
    [InlineData("epub")]
    public void For_GivesTheMinimalValidFileOfPdfCbzAndEpub(string extension)
    {
        var expected = extension switch { "pdf" => BookPlaceholders.Pdf(), "cbz" => BookPlaceholders.Cbz(), _ => BookPlaceholders.Epub() };

        Assert.Equal(expected, BookPlaceholders.For(extension));
    }

    // D51: empty until the trial on 12.4 decides otherwise.
    [Theory]
    [InlineData("azw")]
    [InlineData("azw3")]
    [InlineData("mobi")]
    [InlineData("cbr")]
    [InlineData("cb7")]
    [InlineData("cbt")]
    public void For_GivesAnEmptyFileForTheOtherFormats(string extension)
    {
        Assert.Empty(BookPlaceholders.For(extension));
    }

    [Fact]
    public void Pdf_IsANewCopyEachTime()
    {
        var first = BookPlaceholders.Pdf();
        first[0] = 0;

        Assert.Equal((byte)'%', BookPlaceholders.Pdf()[0]);
    }

    [Fact]
    public void Cbz_HoldsTwoOneByOneJpegPages()
    {
        using var zip = new ZipArchive(new MemoryStream(BookPlaceholders.Cbz()), ZipArchiveMode.Read);

        Assert.Equal(["p1.jpg", "p2.jpg"], zip.Entries.Select(entry => entry.FullName));
        Assert.All(zip.Entries, entry => Assert.Equal((1, 1), JpegSize(Content(entry))));
    }

    [Fact]
    public void Cbz_IsTheSameEveryTime()
    {
        var cbz = BookPlaceholders.Cbz();

        Assert.Equal(cbz, BookPlaceholders.Cbz());
        AssertPlainEntries(cbz);
    }

    [Fact]
    public void Epub_StartsWithTheStoredMimetype()
    {
        var epub = BookPlaceholders.Epub();

        Assert.Equal(0x04034b50u, BitConverter.ToUInt32(epub, 0));
        Assert.Equal(0, BitConverter.ToUInt16(epub, 8));
        Assert.Equal(0, BitConverter.ToUInt16(epub, 28));
        Assert.Equal("mimetypeapplication/epub+zip", Encoding.ASCII.GetString(epub, 30, 28));
    }

    [Fact]
    public void Epub_PointsToAPackageWithNoMetadata()
    {
        using var zip = new ZipArchive(new MemoryStream(BookPlaceholders.Epub()), ZipArchiveMode.Read);
        Assert.Equal(["mimetype", "META-INF/container.xml", "content.opf"], zip.Entries.Select(entry => entry.FullName));

        XNamespace container = "urn:oasis:names:tc:opendocument:xmlns:container";
        var rootfile = XDocument.Parse(Encoding.UTF8.GetString(Content(zip.GetEntry("META-INF/container.xml")!)))
            .Descendants(container + "rootfile")
            .Single();
        Assert.Equal("application/oebps-package+xml", rootfile.Attribute("media-type")?.Value);

        XNamespace opf = "http://www.idpf.org/2007/opf";
        var package = XDocument.Parse(Encoding.UTF8.GetString(Content(zip.GetEntry(rootfile.Attribute("full-path")!.Value)!))).Root!;
        Assert.Equal(opf + "package", package.Name);
        Assert.Empty(package.Element(opf + "metadata")!.Elements());
    }

    [Fact]
    public void Epub_IsTheSameEveryTime()
    {
        var epub = BookPlaceholders.Epub();

        Assert.Equal(epub, BookPlaceholders.Epub());
        AssertPlainEntries(epub);
    }

    // Checks each local header: stored, no data descriptor, no extra field, and the fixed date of 1 January 1980.
    private static void AssertPlainEntries(byte[] zipFile)
    {
        using var zip = new ZipArchive(new MemoryStream(zipFile), ZipArchiveMode.Read);
        var offset = 0;
        foreach (var entry in zip.Entries)
        {
            var header = zipFile.AsSpan(offset);
            Assert.Equal(0x04034b50u, BitConverter.ToUInt32(header));
            Assert.Equal(0, BitConverter.ToUInt16(header[6..]));
            Assert.Equal(0, BitConverter.ToUInt16(header[8..]));
            Assert.Equal(0, BitConverter.ToUInt16(header[28..]));
            Assert.Equal(new DateTime(1980, 1, 1), entry.LastWriteTime.DateTime);
            Assert.Equal(entry.Length, entry.CompressedLength);
            offset += 30 + BitConverter.ToUInt16(header[26..]) + (int)entry.CompressedLength;
        }
    }

    // Reads an entry to its end, so that the archive checks its CRC.
    private static byte[] Content(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    // Walks the JPEG markers up to the frame header and returns its width and height.
    private static (int Width, int Height) JpegSize(byte[] jpeg)
    {
        Assert.Equal([0xFF, 0xD8], jpeg[..2]);
        Assert.Equal([0xFF, 0xD9], jpeg[^2..]);
        var offset = 2;
        while (offset + 4 <= jpeg.Length)
        {
            Assert.Equal(0xFF, jpeg[offset]);
            var marker = jpeg[offset + 1];
            var length = (jpeg[offset + 2] << 8) | jpeg[offset + 3];
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                return ((jpeg[offset + 7] << 8) | jpeg[offset + 8], (jpeg[offset + 5] << 8) | jpeg[offset + 6]);
            }

            offset += 2 + length;
        }

        Assert.Fail("the JPEG has no frame header");
        return default;
    }

    // Reads the objects through startxref and the xref table, checking that each offset lands on its object.
    private static Dictionary<int, string> PdfObjects(string text, out string trailer)
    {
        var tail = Regex.Match(text, @"startxref\n(\d+)\n%%EOF\n$");
        Assert.True(tail.Success, "the file ends with startxref and %%EOF");
        var xref = int.Parse(tail.Groups[1].Value, CultureInfo.InvariantCulture);
        var table = Regex.Match(text[xref..], @"^xref\n0 (\d+)\n((?:\d{10} \d{5} [fn] \n)+)trailer\n(<<.*?>>)\n");
        Assert.True(table.Success, "startxref points at the xref table");

        var size = int.Parse(table.Groups[1].Value, CultureInfo.InvariantCulture);
        var entries = table.Groups[2].Value;
        Assert.Equal(size * 20, entries.Length);
        trailer = table.Groups[3].Value;
        Assert.Equal(size, int.Parse(Regex.Match(trailer, @"/Size (\d+)").Groups[1].Value, CultureInfo.InvariantCulture));

        var objects = new Dictionary<int, string>();
        for (var number = 1; number < size; number++)
        {
            var entry = entries.Substring(number * 20, 20);
            Assert.EndsWith(" n \n", entry, StringComparison.Ordinal);
            var offset = int.Parse(entry[..10], CultureInfo.InvariantCulture);
            var body = Regex.Match(text[offset..], $@"^{number} 0 obj\n(.*?)\nendobj\n", RegexOptions.Singleline);
            Assert.True(body.Success, $"the xref offset of object {number} lands on it");
            objects[number] = body.Groups[1].Value;
        }

        return objects;
    }

    private static int Reference(string dictionary, string key)
    {
        var match = Regex.Match(dictionary, $@"/{key} (\d+) 0 R");
        Assert.True(match.Success, $"/{key} is a reference");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
