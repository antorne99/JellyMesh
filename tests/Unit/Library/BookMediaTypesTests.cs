using Jellyfin.Plugin.JellyMesh.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class BookMediaTypesTests
{
    [Theory]
    [InlineData("Dune.epub", "application/epub+zip")]
    [InlineData("Dune.EPUB", "application/epub+zip")]
    [InlineData("a/b.c/Dune.pdf", "application/pdf")]
    [InlineData("Dune.cbz", "application/vnd.comicbook+zip")]
    [InlineData("Dune.cbr", "application/vnd.comicbook-rar")]
    [InlineData("Dune.cb7", "application/x-cb7")]
    [InlineData("Dune.cbt", "application/x-cbt")]
    [InlineData("Dune.mobi", "application/x-mobipocket-ebook")]
    [InlineData("Dune.azw", "application/vnd.amazon.ebook")]
    [InlineData("Dune.azw3", "application/vnd.amazon.ebook")]
    [InlineData("Dune.html", "application/octet-stream")]
    [InlineData("Dune.svg", "application/octet-stream")]
    [InlineData("Dune", "application/octet-stream")]
    public void AFormatGivesItsMediaTypeAndAnyOtherGoesAsBytes(string path, string mediaType)
    {
        Assert.Equal(mediaType, BookPlaceholders.MediaTypeOf(path));
    }
}
