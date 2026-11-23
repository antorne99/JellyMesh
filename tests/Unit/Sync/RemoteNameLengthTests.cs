using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

// Row 529: the names and artists of a remote are cut to 1000 characters where the item is mapped, for every kind.
public class RemoteNameLengthTests
{
    private const int Limit = 1000;

    // U+1D11E, two UTF-16 units.
    private const string Clef = "\U0001D11E";

    public static TheoryData<RemoteItemType> Types() => new(Enum.GetValues<RemoteItemType>());

    public static TheoryData<RemoteItemType> TypesWithArtists() => new(Enum.GetValues<RemoteItemType>().Except(
        [RemoteItemType.Movie, RemoteItemType.Series, RemoteItemType.Season, RemoteItemType.Episode]));

    private static IndexedItem Map(RemoteItem item, RemoteItemType type)
        => RemoteItemMapper.ToIndexedItem(item, type, "lib", DateTimeOffset.UnixEpoch);

    private static string Text(int length, char last = 'b') => new string('a', length - 1) + last;

    [Theory]
    [MemberData(nameof(Types))]
    public void TheNamesOfEveryKindAreCut(RemoteItemType type)
    {
        var entry = Map(new RemoteItem { Id = "i", Name = Text(Limit + 1), SeriesName = Text(Limit + 2), SeasonName = Text(Limit + 500) }, type);

        var cut = new string('a', Limit);
        Assert.Equal((cut, cut, cut), (entry.Name, entry.SeriesName, entry.SeasonName));
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void NamesOfTheLimitAndNullNamesAreKeptAsSent(RemoteItemType type)
    {
        var atLimit = Text(Limit);
        var kept = Map(new RemoteItem { Id = "i", Name = atLimit, SeriesName = " Show ", SeasonName = string.Empty }, type);
        var absent = Map(new RemoteItem { Id = "i" }, type);

        Assert.Equal((atLimit, " Show ", string.Empty), (kept.Name, kept.SeriesName, kept.SeasonName));
        Assert.Equal(((string?)null, (string?)null, (string?)null), (absent.Name, absent.SeriesName, absent.SeasonName));
    }

    [Theory]
    [MemberData(nameof(TypesWithArtists))]
    public void TheAlbumAndArtistsOfEveryKindThatHasThemAreCut(RemoteItemType type)
    {
        var longText = Text(Limit + 1);
        var cut = new string('a', Limit);
        var entry = Map(
            new RemoteItem
            {
                Id = "i",
                Album = longText,
                Artists = ["Guest", longText],
                AlbumArtists = [new RemoteNamedItem("Band"), new RemoteNamedItem(longText)]
            },
            type);
        var alone = Map(new RemoteItem { Id = "i", AlbumArtist = longText }, type);

        Assert.Equal(cut, entry.Album);
        Assert.Equal(["Guest", cut], entry.Artists!);
        Assert.Equal(["Band", cut], entry.AlbumArtists!);
        Assert.Equal([cut], alone.AlbumArtists!);
    }

    [Theory]
    [InlineData(998, 1000)]
    [InlineData(999, 999)]
    [InlineData(1000, 1000)]
    public void ASurrogatePairIsNeverSplit(int before, int kept)
    {
        var name = new string('a', before) + Clef + "tail";

        var entry = Map(new RemoteItem { Id = "i", Name = name, Artists = [name] }, RemoteItemType.MusicVideo);

        Assert.Equal(name[..kept], entry.Name);
        Assert.Equal(entry.Name, Assert.Single(entry.Artists!));
        Assert.True(entry.Name!.IsNormalized(NormalizationForm.FormD));
    }

    [Fact]
    public void ANameOfSurrogatePairsIsCutBetweenTwoOfThem()
    {
        var even = string.Concat(Enumerable.Repeat(Clef, Limit));

        Assert.Equal(even[..Limit], Map(new RemoteItem { Id = "i", Name = even }, RemoteItemType.Movie).Name);
        Assert.Equal(("x" + even)[..(Limit - 1)], Map(new RemoteItem { Id = "i", Name = "x" + even }, RemoteItemType.Movie).Name);
    }
}
