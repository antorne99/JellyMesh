using System;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class WatchStateTransferTests
{
    private static readonly DateTime _older = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _newer = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid _serverA = Guid.NewGuid();
    private static readonly Guid _serverB = Guid.NewGuid();
    private static readonly Guid _serverC = Guid.NewGuid();

    private static UserItemData Data(bool played = false, DateTime? lastPlayed = null, long position = 0, int playCount = 0) => new()
    {
        Key = "key",
        Played = played,
        LastPlayedDate = lastPlayed,
        PlaybackPositionTicks = position,
        PlayCount = playCount
    };

    [Fact]
    public void ThePositionGoesToTheVersionOfTheLastServer()
    {
        var old = Data(lastPlayed: _newer, position: 600, playCount: 1);
        var a = new WatchStateVersion(_serverA, true, Data());
        var b = new WatchStateVersion(_serverB, false, Data());

        var changed = WatchStateTransfer.Carry(old, _serverB, [_serverA, _serverB], [a, b]);

        Assert.Equal(600, b.Data.PlaybackPositionTicks);
        Assert.Equal(_newer, b.Data.LastPlayedDate);
        Assert.Equal(0, a.Data.PlaybackPositionTicks);
        Assert.Contains(b.Data, changed);
    }

    [Fact]
    public void WithoutALastServerThePositionGoesToTheBestServerThatHasAVersion()
    {
        var old = Data(lastPlayed: _newer, position: 600);
        var a = new WatchStateVersion(_serverA, true, Data());
        var b = new WatchStateVersion(_serverB, false, Data());

        WatchStateTransfer.Carry(old, null, [_serverC, _serverB, _serverA], [a, b]);

        Assert.Equal(600, b.Data.PlaybackPositionTicks);
        Assert.Equal(0, a.Data.PlaybackPositionTicks);
    }

    [Fact]
    public void ALastServerWithNoNewVersionFallsBackToTheBestServer()
    {
        var old = Data(lastPlayed: _newer, position: 600);
        var a = new WatchStateVersion(_serverA, false, Data());
        var b = new WatchStateVersion(_serverB, true, Data());

        WatchStateTransfer.Carry(old, _serverC, [_serverC, _serverA, _serverB], [a, b]);

        Assert.Equal(600, a.Data.PlaybackPositionTicks);
        Assert.Equal(0, b.Data.PlaybackPositionTicks);
    }

    [Fact]
    public void WithNoKnownServerThePositionGoesToThePrimary()
    {
        var old = Data(lastPlayed: _newer, position: 600);
        var local = new WatchStateVersion(null, true, Data());
        var a = new WatchStateVersion(_serverA, false, Data());

        WatchStateTransfer.Carry(old, null, [_serverC], [a, local]);

        Assert.Equal(600, local.Data.PlaybackPositionTicks);
        Assert.Equal(0, a.Data.PlaybackPositionTicks);
    }

    [Fact]
    public void FavoriteRatingAndPlayCountGoToThePrimary()
    {
        var old = Data(lastPlayed: _newer, position: 600, playCount: 3);
        old.IsFavorite = true;
        old.Rating = 8;
        var local = new WatchStateVersion(null, true, Data());
        var a = new WatchStateVersion(_serverA, false, Data());

        var changed = WatchStateTransfer.Carry(old, _serverA, [_serverA], [a, local]);

        Assert.True(local.Data.IsFavorite);
        Assert.Equal(8, local.Data.Rating);
        Assert.Equal(3, local.Data.PlayCount);
        Assert.Equal(_newer, local.Data.LastPlayedDate);
        Assert.Equal(0, local.Data.PlaybackPositionTicks);
        Assert.False(a.Data.IsFavorite);
        Assert.Null(a.Data.Rating);
        Assert.Equal(0, a.Data.PlayCount);
        Assert.Equal(600, a.Data.PlaybackPositionTicks);
        Assert.Equal(2, changed.Count);
    }

    [Fact]
    public void APrimaryThatIsAlsoThePositionTargetTakesEverything()
    {
        var old = Data(lastPlayed: _newer, position: 600, playCount: 2);
        old.IsFavorite = true;
        var a = new WatchStateVersion(_serverA, true, Data());

        var changed = WatchStateTransfer.Carry(old, _serverA, [_serverA], [a]);

        Assert.Equal(600, a.Data.PlaybackPositionTicks);
        Assert.Equal(2, a.Data.PlayCount);
        Assert.True(a.Data.IsFavorite);
        Assert.Equal(_newer, a.Data.LastPlayedDate);
        Assert.Single(changed);
    }

    [Fact]
    public void PlayedReachesEveryVersion()
    {
        var old = Data(played: true, lastPlayed: _newer, playCount: 1);
        var local = new WatchStateVersion(null, true, Data());
        var a = new WatchStateVersion(_serverA, false, Data());
        var b = new WatchStateVersion(_serverB, false, Data());

        var changed = WatchStateTransfer.Carry(old, _serverA, [_serverA, _serverB], [local, a, b]);

        Assert.All([local, a, b], version => Assert.True(version.Data.Played));
        Assert.Equal(3, changed.Count);
        Assert.True(PlayedAlignment.Decide([local.Data, a.Data, b.Data]));
    }

    [Fact]
    public void AnOlderPlayedVersionIsUnmarkedByANewerUnfinishedWatch()
    {
        var old = Data(lastPlayed: _newer, position: 600);
        var a = new WatchStateVersion(_serverA, true, Data(played: true, lastPlayed: _older, playCount: 1));
        var b = new WatchStateVersion(_serverB, false, Data());

        WatchStateTransfer.Carry(old, _serverB, [_serverB, _serverA], [a, b]);

        Assert.False(a.Data.Played);
        Assert.False(b.Data.Played);
        Assert.Equal(600, b.Data.PlaybackPositionTicks);
        Assert.False(PlayedAlignment.Decide([a.Data, b.Data]));
    }

    [Fact]
    public void ANewerStateOnAVersionIsKept()
    {
        var old = Data(played: true, lastPlayed: _older, position: 0, playCount: 4);
        var a = new WatchStateVersion(_serverA, true, Data(lastPlayed: _newer, position: 300, playCount: 1));
        var b = new WatchStateVersion(_serverB, false, Data());

        WatchStateTransfer.Carry(old, _serverA, [_serverA, _serverB], [a, b]);

        Assert.Equal(300, a.Data.PlaybackPositionTicks);
        Assert.Equal(1, a.Data.PlayCount);
        Assert.Equal(_newer, a.Data.LastPlayedDate);
        Assert.False(a.Data.Played);
        Assert.False(b.Data.Played);
    }

    [Fact]
    public void AFavoriteOrRatingAlreadyOnThePrimaryStays()
    {
        var old = Data();
        old.Rating = 3;
        var primary = Data();
        primary.IsFavorite = true;
        primary.Rating = 9;
        var a = new WatchStateVersion(_serverA, true, primary);

        var changed = WatchStateTransfer.Carry(old, null, [_serverA], [a]);

        Assert.True(primary.IsFavorite);
        Assert.Equal(9, primary.Rating);
        Assert.Empty(changed);
    }

    [Fact]
    public void ASecondRunChangesNothing()
    {
        var old = Data(played: true, lastPlayed: _newer, position: 0, playCount: 2);
        old.IsFavorite = true;
        old.Rating = 7;
        var local = new WatchStateVersion(null, true, Data());
        var a = new WatchStateVersion(_serverA, false, Data());

        WatchStateTransfer.Carry(old, _serverA, [_serverA], [local, a]);
        var again = WatchStateTransfer.Carry(old, _serverA, [_serverA], [local, a]);

        Assert.Empty(again);
    }

    [Fact]
    public void AnUntouchedOldItemChangesNothing()
    {
        var a = new WatchStateVersion(_serverA, true, Data(played: true, lastPlayed: _older, playCount: 1));
        var b = new WatchStateVersion(_serverB, false, Data());

        Assert.Empty(WatchStateTransfer.Carry(Data(), _serverB, [_serverB], [a, b]));
        Assert.False(b.Data.Played);
    }

    [Fact]
    public void VersionsWithoutOnePrimaryAreRefused()
    {
        var a = new WatchStateVersion(_serverA, false, Data());
        var b = new WatchStateVersion(_serverB, false, Data());

        Assert.Throws<ArgumentException>(() => WatchStateTransfer.Carry(Data(), null, [], [a, b]));
    }

    [Fact]
    public void ARunCutShortAfterThePositionIsFinishedByTheNext()
    {
        var old = Data(played: true, lastPlayed: _newer, position: 0, playCount: 2);
        old.IsFavorite = true;
        old.Rating = 7;
        var local = new WatchStateVersion(null, true, Data());
        var a = new WatchStateVersion(_serverA, false, Data(played: true, lastPlayed: _newer));
        var b = new WatchStateVersion(_serverB, false, Data());

        var changed = WatchStateTransfer.Carry(old, _serverA, [_serverA, _serverB], [local, a, b]);

        Assert.Equal(2, local.Data.PlayCount);
        Assert.True(local.Data.IsFavorite);
        Assert.Equal(7, local.Data.Rating);
        Assert.All([local, a, b], version => Assert.True(version.Data.Played));
        Assert.Equal(_newer, a.Data.LastPlayedDate);
        Assert.Equal(0, a.Data.PlayCount);
        Assert.DoesNotContain(a.Data, changed);
    }
}
