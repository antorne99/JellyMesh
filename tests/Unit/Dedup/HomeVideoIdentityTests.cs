using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

public class HomeVideoIdentityTests
{
    private static HomeVideoItem Video(string item, string? title = "Beach day", int? year = 2020, double? seconds = 60, string? folder = "Trips") => new()
    {
        ServerId = item[..1],
        ItemId = item,
        Title = title,
        Year = year,
        RunTimeTicks = seconds is { } length ? (long)(length * TimeSpan.TicksPerSecond) : null,
        Folder = folder
    };

    private static List<string> Pairs(HomeVideoItem[] saved, params HomeVideoItem[] current)
        => HomeVideoIdentity.Pair(saved, current).Select(pair => pair.Saved.ItemId + ">" + pair.Current.ItemId).Order(StringComparer.Ordinal).ToList();

    [Theory]
    [InlineData("beach DAY!", 2020, 60.0, "trips", true)]
    [InlineData("Beach day", 2020, 62.0, "Trips", true)]
    [InlineData("Beach day", 2020, 58.0, "Trips", true)]
    [InlineData("Beach day", 2020, 62.1, "Trips", false)]
    [InlineData("Beach day", 2020, null, "Trips", false)]
    [InlineData("Other", 2020, 60.0, "Trips", false)]
    [InlineData("Beach day", 2021, 60.0, "Trips", false)]
    [InlineData("Beach day", null, 60.0, "Trips", false)]
    [InlineData("Beach day", 2020, 60.0, "Other", false)]
    [InlineData("Beach day", 2020, 60.0, null, false)]
    public void TitleYearFolderAndDurationMustAllMatch(string title, int? year, double? seconds, string? folder, bool isSame)
    {
        Assert.Equal(isSame ? ["k1>a1"] : [], Pairs([Video("k1")], Video("a1", title, year, seconds, folder)));
    }

    [Fact]
    public void NoYearOrNoFolderOnEitherSideIsTheSame()
    {
        Assert.Equal(["k1>a1"], Pairs([Video("k1", year: null, folder: null)], Video("a1", year: null, folder: null)));
        Assert.Empty(Pairs([Video("k1", folder: null)], Video("a1")));
        Assert.Empty(Pairs([Video("k1", " ")], Video("a1", " ")));
        Assert.Empty(Pairs([Video("k1", seconds: null)], Video("a1", seconds: null)));
    }

    // A remote may send any number: one that is no duration matches nothing and never fails.
    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MinValue + 1)]
    [InlineData(-1)]
    [InlineData(0)]
    public void ADurationThatIsNotPositiveMatchesNothing(long ticks)
    {
        var wrong = Video("a1") with { RunTimeTicks = ticks };

        Assert.Empty(Pairs([Video("k1"), Video("k2", seconds: 1), wrong with { ItemId = "k3" }], wrong));
        Assert.Empty(Pairs([Video("k1")], Video("a1") with { RunTimeTicks = long.MaxValue }));
    }

    [Fact]
    public void EachSavedVideoFindsEveryCopyOfItsOwn()
    {
        Assert.Equal(["k1>a1", "k1>b1", "k2>a2"], Pairs([Video("k1"), Video("k2", seconds: 90)], Video("a1"), Video("a2", seconds: 91), Video("b1")));
    }
}
