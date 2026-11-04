using System;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class WatchStateMergeTests
{
    private static readonly DateTime _older = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _newer = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    private static UserItemData Data() => new() { Key = "key" };

    [Fact]
    public void AnUntouchedLocalCopyTakesTheState()
    {
        var stub = Data();
        stub.Played = true;
        stub.PlayCount = 2;
        stub.PlaybackPositionTicks = 100;
        stub.LastPlayedDate = _older;
        var local = Data();

        Assert.True(WatchStateMerge.Merge(stub, local));

        Assert.True(local.Played);
        Assert.Equal(2, local.PlayCount);
        Assert.Equal(100, local.PlaybackPositionTicks);
        Assert.Equal(_older, local.LastPlayedDate);
    }

    [Fact]
    public void ANewerStubWins()
    {
        var stub = Data();
        stub.PlaybackPositionTicks = 100;
        stub.LastPlayedDate = _newer;
        var local = Data();
        local.Played = true;
        local.PlayCount = 1;
        local.LastPlayedDate = _older;

        Assert.True(WatchStateMerge.Merge(stub, local));

        Assert.False(local.Played);
        Assert.Equal(100, local.PlaybackPositionTicks);
        Assert.Equal(_newer, local.LastPlayedDate);
    }

    [Fact]
    public void ANewerLocalCopyWins()
    {
        var stub = Data();
        stub.Played = true;
        stub.LastPlayedDate = _older;
        var local = Data();
        local.PlaybackPositionTicks = 50;
        local.LastPlayedDate = _newer;

        Assert.False(WatchStateMerge.Merge(stub, local));

        Assert.False(local.Played);
        Assert.Equal(50, local.PlaybackPositionTicks);
        Assert.Equal(_newer, local.LastPlayedDate);
    }

    [Fact]
    public void AStubNeverPlayedChangesNothing()
    {
        var local = Data();
        local.Played = true;

        Assert.False(WatchStateMerge.Merge(Data(), local));

        Assert.True(local.Played);
    }

    [Fact]
    public void AStateWithoutADateLosesToADatedOne()
    {
        var stub = Data();
        stub.Played = true;
        var local = Data();
        local.LastPlayedDate = _older;

        Assert.False(WatchStateMerge.Merge(stub, local));

        Assert.False(local.Played);
    }

    [Fact]
    public void AFavoriteStaysAndARatingFillsAGap()
    {
        var stub = Data();
        stub.IsFavorite = true;
        stub.Rating = 8;
        var local = Data();

        Assert.True(WatchStateMerge.Merge(stub, local));

        Assert.True(local.IsFavorite);
        Assert.Equal(8, local.Rating);
    }

    [Fact]
    public void ALocalRatingIsKept()
    {
        var stub = Data();
        stub.Rating = 8;
        var local = Data();
        local.Rating = 3;

        Assert.False(WatchStateMerge.Merge(stub, local));

        Assert.Equal(3, local.Rating);
    }

    [Fact]
    public void ASeriesRowHandsOverOnlyItsFavorite()
    {
        var stub = Data();
        stub.IsFavorite = true;
        stub.Played = true;
        var local = Data();

        Assert.True(WatchStateMerge.MergeFavorite(stub, local));
        Assert.False(WatchStateMerge.MergeFavorite(stub, local));

        Assert.True(local.IsFavorite);
        Assert.False(local.Played);
    }
}
