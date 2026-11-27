using System;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.JellyMesh.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class PhotoPlaceholdersTests
{
    public static TheoryData<string, byte[]> Signatures => new()
    {
        { "jpg", [0xFF, 0xD8, 0xFF] },
        { "jpeg", [0xFF, 0xD8, 0xFF] },
        { "JPG", [0xFF, 0xD8, 0xFF] },
        { "png", [0x89, (byte)'P', (byte)'N', (byte)'G'] },
        { "gif", "GIF89a"u8.ToArray() },
        { "bmp", "BM"u8.ToArray() },
        { "webp", "RIFF"u8.ToArray() },
        { "tif", "II*"u8.ToArray() },
        { "tiff", "II*"u8.ToArray() },
        { "heic", [0xFF, 0xD8, 0xFF] },
        { "cr2", [0xFF, 0xD8, 0xFF] }
    };

    [Theory]
    [MemberData(nameof(Signatures))]
    public void APhotoPlaceholderIsAnImageOfItsFormatOrAJpeg(string extension, byte[] signature)
    {
        Assert.Equal(signature, PhotoPlaceholders.For(extension).Take(signature.Length));
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("png")]
    [InlineData("gif")]
    [InlineData("bmp")]
    [InlineData("webp")]
    [InlineData("tif")]
    [InlineData("heic")]
    public void APhotoPlaceholderCarriesNoExif(string extension)
    {
        var bytes = PhotoPlaceholders.For(extension);

        Assert.DoesNotContain("Exif", Encoding.Latin1.GetString(bytes), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(bytes.Zip(bytes.Skip(1)), pair => pair is (0xFF, 0xE1));
        Assert.True(bytes.Length < 1024);
    }

    [Fact]
    public void EachCallGivesANewCopy()
    {
        var first = PhotoPlaceholders.For("png");
        first[0] = 0;

        Assert.Equal(0x89, PhotoPlaceholders.For("png")[0]);
    }
}
