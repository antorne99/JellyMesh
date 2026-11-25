using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

public class PhotoIdentityTests
{
    private static PhotoItem Photo(string item, string? title = "Beach", int? year = 2020, string? folder = "Trips") => new()
    {
        ServerId = item[..1],
        ItemId = item,
        Title = title,
        Year = year,
        Folder = folder
    };

    private static List<string> Pairs(PhotoItem[] saved, params PhotoItem[] current)
        => PhotoIdentity.Pair(saved, current, 100, CancellationToken.None).Pairs.Select(pair => pair.Saved.ItemId + ">" + pair.Current.ItemId).Order(StringComparer.Ordinal).ToList();

    [Theory]
    [InlineData("beach!", 2020, "trips", true)]
    [InlineData("Beach", 2020, "Trips", true)]
    [InlineData("Other", 2020, "Trips", false)]
    [InlineData("Beach", 2021, "Trips", false)]
    [InlineData("Beach", null, "Trips", false)]
    [InlineData("Beach", 2020, "Other", false)]
    [InlineData("Beach", 2020, null, false)]
    public void TitleYearAndFolderMustAllMatch(string title, int? year, string? folder, bool isSame)
    {
        Assert.Equal(isSame ? ["k1>a1"] : [], Pairs([Photo("k1")], Photo("a1", title, year, folder)));
    }

    [Fact]
    public void NoYearOrNoFolderOnEitherSideIsTheSameButNoTitleIsNothing()
    {
        Assert.Equal(["k1>a1"], Pairs([Photo("k1", year: null, folder: null)], Photo("a1", year: null, folder: null)));
        Assert.Empty(Pairs([Photo("k1", folder: null)], Photo("a1")));
        Assert.Empty(Pairs([Photo("k1", " ")], Photo("a1", " ")));
        Assert.Empty(Pairs([Photo("k1", null)], Photo("a1", null)));
    }

    [Fact]
    public void EachSavedPhotoFindsEveryCopyOfItsOwn()
    {
        Assert.Equal(["k1>a1", "k1>b1", "k2>a2"], Pairs([Photo("k1"), Photo("k2", "Sunset")], Photo("a1"), Photo("a2", "Sunset"), Photo("b1")));
    }

    // A photo in two folders is known by either, and one in many places is one copy.
    [Theory]
    [InlineData("Other", "Trips")]
    [InlineData("Trips", "Other")]
    public void APhotoInTwoFoldersIsFoundByEither(string first, string second)
        => Assert.Equal(["k1>a1"], Pairs([Photo("k1")], Photo("a1", folder: first), Photo("a1", folder: second), Photo("a1")));

    // Row 542: a title with too many copies is left as it is, and the others go on.
    [Fact]
    public void APhotoWithMoreCopiesThanTheMostIsLeftAndTheOthersArePaired()
    {
        var found = PhotoIdentity.Pair(
            [Photo("k1"), Photo("k2", "Other")],
            [Photo("a1"), Photo("a2"), Photo("a3"), Photo("b1", "Other"), Photo("b2", "Other")],
            2,
            CancellationToken.None);

        Assert.Equal(1, found.Left);
        Assert.Equal([("k2", "b1"), ("k2", "b2")], found.Pairs.Select(pair => (pair.Saved.ItemId, pair.Current.ItemId)));
    }

    [Fact]
    public void ThePairingOfPhotosCanBeCancelled()
        => Assert.Throws<OperationCanceledException>(() => PhotoIdentity.Pair([Photo("k1")], [Photo("a1")], 100, new CancellationToken(true)));
}
