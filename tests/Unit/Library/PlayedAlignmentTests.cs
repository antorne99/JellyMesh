using System;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class PlayedAlignmentTests
{
    private static readonly DateTime _older = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _newer = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    private static UserItemData Data(bool played = false, DateTime? lastPlayed = null, long position = 0, int playCount = 0) => new()
    {
        Key = "key",
        Played = played,
        LastPlayedDate = lastPlayed,
        PlaybackPositionTicks = position,
        PlayCount = playCount
    };

    [Fact]
    public void NobodyPlayedLeavesTheFlagsAlone()
    {
        Assert.Null(PlayedAlignment.Decide([Data(), Data(), null]));
    }

    [Fact]
    public void APlayedLocalCopyMarksAnUnplayedStub()
    {
        Assert.True(PlayedAlignment.Decide([Data(played: true, lastPlayed: _older, playCount: 1), Data()]));
    }

    [Fact]
    public void TheVersionPlayedLastWinsEvenWhenItIsNotPlayed()
    {
        Assert.False(PlayedAlignment.Decide([Data(played: true, lastPlayed: _older), Data(lastPlayed: _newer, position: 100)]));
    }

    [Fact]
    public void ANewerPlayedVersionWins()
    {
        Assert.True(PlayedAlignment.Decide([Data(lastPlayed: _older, position: 100), Data(played: true, lastPlayed: _newer)]));
    }

    [Fact]
    public void AVersionWithADateBeatsOneWithout()
    {
        Assert.False(PlayedAlignment.Decide([Data(played: true), Data(lastPlayed: _older, position: 10)]));
    }

    [Fact]
    public void WithoutAnyDateAPlayedVersionWins()
    {
        Assert.True(PlayedAlignment.Decide([Data(position: 10), Data(played: true)]));
    }
}
