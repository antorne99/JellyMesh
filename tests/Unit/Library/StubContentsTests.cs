using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class StubContentsTests
{
    private static readonly Guid _server = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid _other = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");
    private static readonly Guid _firstLocal = Guid.Parse("cccccccc-0000-0000-0000-000000000000");
    private static readonly Guid _secondLocal = Guid.Parse("dddddddd-0000-0000-0000-000000000000");

    private static ResolvedSource Source(Guid server, string item) => new(
        server,
        "Server",
        ServerSyncState.Complete,
        item,
        item,
        null,
        new SourceQuality(1080, false, 1000, "h264"),
        DateTimeOffset.UnixEpoch);

    private static ContentIdentity Identity(int season, int episode, string work = "w") =>
        new($"{work}|S{season}E{episode}", work, ContentKind.Episode, MatchKind.ProviderId, season, episode);

    private static ResolvedContent Won(int episode, ResolvedSource winner, params ResolvedSource[] backups) =>
        new(Identity(1, episode), false, winner, backups)
        {
            RemoteItems = backups.Prepend(winner).Select(source => new ResolvedItem(source.ServerId, source.ItemId)).ToList()
        };

    private static ResolvedContent Local(int episode, Guid localId, params string[] remoteItems) =>
        new(Identity(1, episode), true, null, [])
        {
            LocalItemIds = [localId],
            RemoteItems = remoteItems.Select(item => new ResolvedItem(_server, item)).ToList()
        };

    [Fact]
    public void ConsecutiveEpisodesOfOneFile_BecomeOneContent()
    {
        var content = Assert.Single(StubContents.Group([Won(2, Source(_server, "f")), Won(1, Source(_server, "f")), Won(3, Source(_server, "f"))]));

        Assert.Equal(1, content.Identity.EpisodeNumber);
        Assert.Equal(3, content.EpisodeNumberEnd);
        Assert.Equal("w|S1E1", content.Identity.Key);
        Assert.Equal([new ResolvedItem(_server, "f")], content.RemoteItems);
    }

    [Fact]
    public void AnEpisodeAlone_IsLeftAsItIs()
    {
        var alone = Won(1, Source(_server, "f"));

        Assert.Same(alone, Assert.Single(StubContents.Group([alone])));
        Assert.Null(alone.EpisodeNumberEnd);
    }

    [Fact]
    public void DifferentWinners_StaySeparate()
    {
        Assert.Equal(2, StubContents.Group([Won(1, Source(_server, "a")), Won(2, Source(_server, "b"))]).Count);
    }

    [Fact]
    public void AnEpisodeInBetween_SplitsARange()
    {
        var groups = StubContents.Group([Won(1, Source(_server, "f")), Local(2, _firstLocal, "f"), Won(3, Source(_server, "f"))]);

        Assert.Equal(3, groups.Count);
        Assert.All(groups, group => Assert.Null(group.EpisodeNumberEnd));
    }

    [Fact]
    public void AGapInTheNumbers_SplitsARange()
    {
        Assert.Equal(2, StubContents.Group([Won(1, Source(_server, "f")), Won(3, Source(_server, "f"))]).Count);
    }

    [Fact]
    public void OtherSeasonsAndWorks_StaySeparate()
    {
        var season = new ResolvedContent(Identity(2, 2), false, Source(_server, "f"), []);
        var work = new ResolvedContent(Identity(1, 2, "x"), false, Source(_server, "f"), []);

        Assert.Equal(3, StubContents.Group([Won(1, Source(_server, "f")), season, work]).Count);
    }

    [Fact]
    public void Films_AreNeverGrouped()
    {
        var first = new ResolvedContent(new ContentIdentity("a", "a", ContentKind.Movie, MatchKind.ProviderId), false, Source(_server, "f"), []);
        var second = new ResolvedContent(new ContentIdentity("b", "b", ContentKind.Movie, MatchKind.ProviderId), false, Source(_server, "f"), []);

        Assert.Equal(2, StubContents.Group([first, second]).Count);
    }

    [Fact]
    public void OnlySourcesCoveringTheWholeRange_StayAsBackups()
    {
        var content = Assert.Single(StubContents.Group([
            Won(1, Source(_server, "f"), Source(_other, "single"), Source(_other, "double")),
            Won(2, Source(_server, "f"), Source(_other, "double"))
        ]));

        Assert.Equal("double", Assert.Single(content.Backups).ItemId);
        Assert.Equal(3, content.RemoteItems.Count);
    }

    [Fact]
    public void LocalEpisodesSharingARemoteFile_AreGrouped_WithBothLocalCopies()
    {
        var content = Assert.Single(StubContents.Group([Local(1, _firstLocal, "f"), Local(2, _secondLocal, "f")]));

        Assert.True(content.IsLocal);
        Assert.Equal(2, content.EpisodeNumberEnd);
        Assert.Equal([_firstLocal, _secondLocal], content.LocalItemIds);
    }

    [Fact]
    public void LocalEpisodesWithNothingInCommon_StaySeparate()
    {
        Assert.Equal(2, StubContents.Group([Local(1, _firstLocal, "a"), Local(2, _secondLocal, "b")]).Count);
    }

    [Fact]
    public void ALocalEpisode_DoesNotJoinARemoteOne()
    {
        Assert.Equal(2, StubContents.Group([Local(1, _firstLocal, "f"), Won(2, Source(_server, "f"))]).Count);
    }

    [Fact]
    public void Groups_DoNotDependOnOrder()
    {
        IReadOnlyList<ResolvedContent> contents =
        [
            Won(1, Source(_server, "f")),
            Won(2, Source(_server, "f")),
            Local(3, _firstLocal, "f"),
            Won(4, Source(_other, "g")),
        ];

        var expected = StubContents.Group(contents).Select(group => (group.Identity.Key, group.EpisodeNumberEnd)).ToList();

        Assert.Equal(expected, StubContents.Group(contents.Reverse()).Select(group => (group.Identity.Key, group.EpisodeNumberEnd)));
    }
}
