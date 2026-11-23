using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

public class TrackDedupTests
{
    private const long Second = TimeSpan.TicksPerSecond;

    private static TrackItem Track(
        string server,
        string item,
        string? recording = null,
        string? trackId = null,
        string? title = "Song",
        string? album = "Album",
        string[]? albumArtists = null,
        string[]? artists = null,
        int? disc = null,
        int? number = 1,
        long? seconds = 180,
        string codec = "flac",
        int? bitDepth = 16,
        int? bitRate = 900_000,
        bool playable = true) => new()
    {
        ServerId = server,
        ItemId = item,
        Title = title,
        Album = album,
        AlbumArtists = albumArtists ?? ["Artist"],
        Artists = artists ?? ["Artist"],
        DiscNumber = disc,
        TrackNumber = number,
        RunTimeTicks = seconds * Second,
        RecordingId = recording,
        TrackId = trackId,
        Versions = playable ? [new MediaVersion("ms-" + item, SourceQuality.Audio(codec, bitDepth, 44100, bitRate))] : []
    };

    private static IReadOnlyList<RemoteItemKey> Items(FederationDecision decision) => decision.RemoteItems;

    private static string[] Shape(IReadOnlyList<FederationDecision> decisions) => decisions
        .Select(decision => string.Join(
            " ",
            [decision.Identity.ToString(), .. decision.Backups.Prepend(decision.Winner!).Select(source => source.ToString()), .. decision.RemoteItems.Select(item => item.ToString())]))
        .ToArray();

    private static FederationDecision Holding(IReadOnlyList<FederationDecision> decisions, string item)
        => Assert.Single(decisions, decision => decision.RemoteItems.Any(key => key.ItemId == item));

    [Fact]
    public void TheSameRecordingOnTwoServersIsOneTrackWithTheFlacAsWinnerAndTheMp3AsBackup()
    {
        var mp3 = Track("a", "a1", recording: "rec-1", codec: "mp3", bitDepth: null, bitRate: 320_000);
        var flac = Track("b", "b1", recording: "rec-1");

        var decision = Assert.Single(TrackDedup.Resolve([mp3, flac]));

        Assert.Equal(ContentKind.Track, decision.Identity.Kind);
        Assert.Equal(MatchKind.ProviderId, decision.Identity.Match);
        Assert.Equal("b", decision.Winner!.ServerId);
        Assert.Equal(["a"], decision.Backups.Select(source => source.ServerId));
        Assert.Equal([new RemoteItemKey("a", "a1"), new RemoteItemKey("b", "b1")], Items(decision));
        Assert.False(decision.IsLocal);
    }

    [Fact]
    public void TheSameMusicBrainzTrackIdIsOneTrackEvenWithOtherTags()
    {
        var decisions = TrackDedup.Resolve([Track("a", "a1", trackId: "trk-1", title: "One"), Track("b", "b1", trackId: "TRK-1", title: "Other", album: "Else")]);

        Assert.Equal(2, Items(Assert.Single(decisions)).Count);
    }

    [Fact]
    public void DifferentRecordingsStayApartEvenWithTheSameTags()
    {
        var decisions = TrackDedup.Resolve([Track("a", "a1", recording: "rec-1"), Track("b", "b1", recording: "rec-2")]);

        Assert.Equal(2, decisions.Count);
    }

    [Fact]
    public void ASharedTrackIdNeverJoinsTwoRecordings()
    {
        var decisions = TrackDedup.Resolve([Track("a", "a1", recording: "rec-1", trackId: "trk-1"), Track("b", "b1", recording: "rec-2", trackId: "trk-1")]);

        Assert.Equal(["Track|mbr:rec-1", "Track|mbr:rec-2"], decisions.Select(decision => decision.Identity.Key));
    }

    [Fact]
    public void DifferentTrackIdsLeaveTheTagsToDecide()
    {
        var decisions = TrackDedup.Resolve([Track("a", "a1", trackId: "trk-1"), Track("b", "b1", trackId: "trk-2")]);

        Assert.Equal(MatchKind.ProviderId, Assert.Single(decisions).Identity.Match);
    }

    [Fact]
    public void ACopyWithNoIdsJoinsOneRecordingAndNeverBringsTwoTogether()
    {
        var decisions = TrackDedup.Resolve([Track("a", "a1", recording: "rec-1"), Track("b", "b1", recording: "rec-2"), Track("c", "c1")]);

        Assert.Equal(2, decisions.Count);
        Assert.All(decisions, decision => Assert.True(decision.RemoteItems.Count(key => key.ServerId is "a" or "b") == 1));
    }

    [Fact]
    public void WithoutIdsTheAlbumPlaceAndNormalizedNamesMatch()
    {
        var one = Track("a", "a1", title: "Hey Jude!", album: "The Álbum", albumArtists: ["The Beatles"], disc: 1);
        var two = Track("b", "b1", title: "hey jude", album: "album", albumArtists: ["Beatles"], disc: null);

        var decision = Assert.Single(TrackDedup.Resolve([one, two]));

        Assert.Equal(MatchKind.AlbumPosition, decision.Identity.Match);
    }

    [Fact]
    public void TheTrackArtistsStandForMissingAlbumArtists()
    {
        var decisions = TrackDedup.Resolve([
            Track("a", "a1", albumArtists: [], artists: ["Singer"]),
            Track("b", "b1", albumArtists: ["Singer"], artists: ["Singer", "Guest"])
        ]);

        Assert.Single(decisions);
    }

    [Theory]
    [InlineData("Other", "Album", "Artist", 1, 1)]
    [InlineData("Song", "Other", "Artist", 1, 1)]
    [InlineData("Song", "Album", "Other", 1, 1)]
    [InlineData("Song", "Album", "Artist", 2, 1)]
    [InlineData("Song", "Album", "Artist", 1, 2)]
    public void AnyOtherTagKeepsTwoCopiesApart(string title, string album, string artist, int disc, int number)
    {
        var decisions = TrackDedup.Resolve([Track("a", "a1"), Track("b", "b1", title: title, album: album, albumArtists: [artist], disc: disc, number: number)]);

        Assert.Equal(2, decisions.Count);
    }

    [Theory]
    [InlineData(182, 1)]
    [InlineData(183, 2)]
    public void DurationsMustBeWithinTwoSeconds(long seconds, int expected)
    {
        var decisions = TrackDedup.Resolve([Track("a", "a1", seconds: 180), Track("b", "b1", seconds: seconds)]);

        Assert.Equal(expected, decisions.Count);
    }

    [Fact]
    public void ChainedDurationsJoinWhateverTheOrder()
    {
        TrackItem[] tracks = [Track("a", "a1", seconds: 180), Track("b", "b1", seconds: 182), Track("c", "c1", seconds: 184)];

        foreach (var order in Fixtures.Permutations<TrackItem>(tracks))
        {
            var decision = Assert.Single(TrackDedup.Resolve(order));
            Assert.Equal(3, decision.RemoteItems.Count);
            Assert.Equal(Shape(TrackDedup.Resolve(tracks)), Shape([decision]));
        }
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void WithoutNumberAlbumOrDurationACopyStandsAloneUnlessItsIdsMatch(bool noNumber, bool noAlbum, bool noDuration)
    {
        var lacking = Track("b", "b1", number: noNumber ? null : 1, album: noAlbum ? null : "Album", seconds: noDuration ? null : 180);

        var alone = TrackDedup.Resolve([Track("a", "a1"), lacking]);
        var byId = TrackDedup.Resolve([Track("a", "a1", recording: "rec-1"), lacking with { RecordingId = "rec-1" }]);

        Assert.Equal(2, alone.Count);
        Assert.Equal(MatchKind.ItemOnly, Holding(alone, "b1").Identity.Match);
        Assert.Equal("Track|item:b:b1", Holding(alone, "b1").Identity.Key);
        Assert.Single(byId);
    }

    [Fact]
    public void TwoCopiesOnOneServerAreOneTrackWithTheOtherAsBackup()
    {
        var decision = Assert.Single(TrackDedup.Resolve([
            Track("a", "a1", recording: "rec-1", codec: "mp3", bitDepth: null, bitRate: 320_000),
            Track("a", "a2", recording: "rec-1")
        ]));

        Assert.Equal("a2", decision.Winner!.ItemId);
        Assert.Equal(["a1"], decision.Backups.Select(source => source.ItemId));
    }

    [Fact]
    public void ATrackWithNoVersionIsNotImported()
    {
        Assert.Empty(TrackDedup.Resolve([Track("a", "a1", playable: false)]));
    }

    [Fact]
    public void TheResultDoesNotDependOnTheOrderOfTheCatalog()
    {
        TrackItem[] tracks =
        [
            Track("a", "a1", recording: "rec-1"),
            Track("b", "b1", trackId: "trk-1", codec: "mp3", bitDepth: null),
            Track("c", "c1", recording: "rec-1", trackId: "trk-1", codec: "alac"),
            Track("c", "c2", title: "Other"),
            Track("a", "a3", title: "Other", seconds: 181),
        ];
        var expected = TrackDedup.Resolve(tracks);
        var random = new Random(7);

        for (var run = 0; run < 20; run++)
        {
            Assert.Equal(Shape(expected), Shape(TrackDedup.Resolve(Fixtures.Shuffled(tracks, random))));
        }

        Assert.Equal(2, expected.Count);
        Assert.Equal("Track|mbr:rec-1", Holding(expected, "a1").Identity.Key);
        Assert.Equal(["a", "c", "b"], Holding(expected, "a1").Backups.Prepend(Holding(expected, "a1").Winner!).Select(source => source.ServerId));
    }

    private static string[] Pairs(TrackItem[] saved, params TrackItem[] current)
        => TrackDedup.Pair(saved, current).Select(pair => pair.Saved.ItemId + ">" + pair.Current.ServerId + ":" + pair.Current.ItemId).Order(StringComparer.Ordinal).ToArray();

    // Saved tracks have no version to rank.
    private static TrackItem Saved(string item, string? recording = null, string? trackId = null, string title = "Song", long? seconds = 180)
        => Track("kept", item, recording, trackId, title, seconds: seconds, playable: false);

    [Fact]
    public void ASavedTrackPairsWithEveryCurrentCopyByRecordingByTrackIdAndByPlace()
    {
        Assert.Equal(["1>a:a1", "1>b:b1"], Pairs([Saved("1", recording: "rec-1", title: "Old")], Track("a", "a1", recording: "REC-1"), Track("b", "b1", recording: "rec-1"), Track("b", "b2", recording: "rec-2")));
        Assert.Equal(["1>a:a1", "1>b:b1"], Pairs([Saved("1", trackId: "trk-1", title: "Old")], Track("a", "a1", trackId: "trk-1"), Track("b", "b1"), Track("b", "b2", title: "Other")));
        Assert.Equal(["1>a:a1", "2>a:a2"], Pairs([Saved("1"), Saved("2", title: "Other")], Track("a", "a1", seconds: 181), Track("a", "a2", title: "Other"), Track("b", "b1", seconds: 190)));
    }

    [Fact]
    public void ASavedTrackPairsWithNoOtherRecordingAndWithoutADurationWithNoPlace()
    {
        Assert.Empty(Pairs([Saved("1", recording: "rec-1")], Track("a", "a1", recording: "rec-2")));
        Assert.Empty(Pairs([Saved("1", seconds: null)], Track("a", "a1")));
        Assert.Empty(Pairs([Saved("1", recording: "rec-1"), Saved("2", recording: "rec-1")]));
    }
}
