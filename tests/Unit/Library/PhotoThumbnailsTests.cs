using System;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.JellyMesh.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The thumbnail of a remote photo is saved only as a whole JPEG with no APP1 segment, so no EXIF or GPS reaches the disk.
public sealed class PhotoThumbnailsTests
{
    private static readonly byte[] _scan = [0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56];

    private static byte[] Segment(byte marker, byte[] payload)
        => [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)((payload.Length + 2) & 0xFF), .. payload];

    private static byte[] Jpeg(params byte[][] segments) => [0xFF, 0xD8, .. segments.SelectMany(segment => segment), .. _scan, 0xFF, 0xD9];

    private static byte[] Jfif => Segment(0xE0, [.. Encoding.ASCII.GetBytes("JFIF\0"), 1, 1, 0, 0, 1, 0, 1, 0, 0]);

    [Fact]
    public void AWholeJpegWithoutApp1IsClean()
    {
        Assert.True(PhotoThumbnails.IsCleanJpeg(Jpeg(Jfif, Segment(0xDB, new byte[65]))));
        Assert.True(PhotoThumbnails.IsCleanJpeg(PhotoPlaceholders.For("jpg")));
    }

    // FF E1 inside the payload of another segment, or in the stuffed scan data, is not a marker.
    [Fact]
    public void TheBytesOfAnApp1InsideAnotherSegmentAreNotOne()
    {
        Assert.True(PhotoThumbnails.IsCleanJpeg(Jpeg(Jfif, Segment(0xE0, [0xFF, 0xE1, 0x00, 0x10]))));
    }

    [Theory]
    [InlineData("Exif\0\0")]
    [InlineData("http://ns.adobe.com/xap/1.0/\0")]
    public void AJpegWithAnApp1IsRefused(string header)
    {
        var app1 = Segment(0xE1, [.. Encoding.ASCII.GetBytes(header), 0x4D, 0x4D]);

        Assert.False(PhotoThumbnails.IsCleanJpeg(Jpeg(Jfif, app1)));
        Assert.False(PhotoThumbnails.IsCleanJpeg(Jpeg(app1)));
    }

    // Between scans of a progressive JPEG an APP1 is still a marker.
    [Fact]
    public void AnApp1AfterTheFirstScanIsRefused()
    {
        byte[] bytes = [.. Jpeg(Jfif)[..^2], .. Segment(0xE1, Encoding.ASCII.GetBytes("Exif\0\0")), .. _scan, 0xFF, 0xD9];

        Assert.False(PhotoThumbnails.IsCleanJpeg(bytes));
    }

    // A restart, TEM or SOI marker has no length: read as one, it would hide the APP1 after it.
    [Theory]
    [InlineData(0xD0)]
    [InlineData(0x01)]
    [InlineData(0xD8)]
    public void AMarkerWithNoLengthOutsideAScanIsRefused(byte marker)
    {
        // Read as a length, FF E1 skips exactly this APP1 of 0xFFE1 bytes.
        var app1 = Segment(0xE1, [.. Encoding.ASCII.GetBytes("Exif\0\0"), .. new byte[0xFFE1 - 4 - 6]]);

        Assert.False(PhotoThumbnails.IsCleanJpeg(Jpeg(Jfif, [0xFF, marker, .. app1])));
    }

    // Only the segments of a plain image pass: IPTC, comments and other application data may carry a position.
    [Theory]
    [InlineData(0xED, false)]
    [InlineData(0xFE, false)]
    [InlineData(0xEF, false)]
    [InlineData(0xE2, true)]
    [InlineData(0xEE, true)]
    [InlineData(0xDD, true)]
    [InlineData(0xC4, true)]
    [InlineData(0xC2, true)]
    public void OnlyTheSegmentsOfAPlainImageAreAllowed(byte marker, bool allowed)
    {
        Assert.Equal(allowed, PhotoThumbnails.IsCleanJpeg(Jpeg(Jfif, Segment(marker, [0x00, 0x04]))));
    }

    [Fact]
    public void WhatIsNotAWholeJpegIsRefused()
    {
        var jpeg = Jpeg(Jfif);

        Assert.False(PhotoThumbnails.IsCleanJpeg([]));
        Assert.False(PhotoThumbnails.IsCleanJpeg(PhotoPlaceholders.For("png")));
        Assert.False(PhotoThumbnails.IsCleanJpeg(jpeg[..^2]));
        Assert.False(PhotoThumbnails.IsCleanJpeg(jpeg[..10]));
        Assert.False(PhotoThumbnails.IsCleanJpeg([0xFF, 0xD8, 0xFF, 0xD9]));
        Assert.False(PhotoThumbnails.IsCleanJpeg([0xFF, 0xD8, 0x00, 0xFF, 0xD9]));
    }

    [Fact]
    public void TheThumbnailOfAStubIsInAHiddenFolderOfTheRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "federata");

        Assert.Equal(Path.Combine(root, ".jellymesh-thumbs", "0123abcd.jpg"), PhotoThumbnails.PathOf(root, "0123abcd"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("a/b")]
    [InlineData("a\0b")]
    public void AnIdThatIsNotAFileNameHasNoThumbnail(string id)
    {
        Assert.Null(PhotoThumbnails.PathOf("/federata", id));
    }

    [Fact]
    public void ASegmentLongerThanTheFileIsRefused()
    {
        Assert.False(PhotoThumbnails.IsCleanJpeg([0xFF, 0xD8, 0xFF, 0xE0, 0x40, 0x00, 0x01, 0xFF, 0xD9]));
    }
}
