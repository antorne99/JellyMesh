using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

/// <summary>Which stub a content gets, and which contents a stub left over is merged into, for files of several episodes (§10 row 537).</summary>
public class StubMatcherTests
{
    private static readonly Guid _server = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");

    // Row 537: the key of E10 changed, and its file of E10-E11 is an item of both groups.
    [Fact]
    public void AStubOfAnotherKey_JoinsTheGroupThatCoversItsEpisode()
    {
        var stubs = new[] { Stub("s9", "old9", 9, null, "w"), Stub("s10", "old10", 10, null, "x"), Stub("s11", "new11", 11, null, "x") };

        var claims = new StubMatcher(stubs).ClaimAll([Content("new11", 11, null, "x"), Content("new9", 9, 10, "w", "x")]);

        Assert.Equal("s9", ClaimOf(claims, "new9").Stub?.Id);
        Assert.Equal("s11", ClaimOf(claims, "new11").Stub?.Id);
        Assert.Equal("s10", Assert.Single(ClaimOf(claims, "new9").Merged).Id);
        Assert.Empty(ClaimOf(claims, "new11").Merged);
    }

    [Fact]
    public void AGroupWithNoStub_LeavesTheStubOfAnEpisodeThatAnotherGroupCovers()
    {
        var stubs = new[] { Stub("s9", "old9", 9, null, "w"), Stub("s10", "old10", 10, null, "x") };

        var claims = new StubMatcher(stubs).ClaimAll([Content("new9", 9, 10, "w", "x"), Content("a-new11", 11, null, "x")]);

        Assert.Equal("s9", ClaimOf(claims, "new9").Stub?.Id);
        Assert.Null(ClaimOf(claims, "a-new11").Stub);
        Assert.Equal("s10", Assert.Single(ClaimOf(claims, "new9").Merged).Id);
    }

    [Fact]
    public void AContentOfAnotherKey_GetsTheStubOfItsOwnEpisode()
    {
        var stubs = new[] { Stub("s2", "old2", 2, null, "x"), Stub("s1", "old1", 1, null, "x") };

        var claims = new StubMatcher(stubs).ClaimAll([Content("new1", 1, null, "x"), Content("new2", 2, null, "x")]);

        Assert.Equal("s1", ClaimOf(claims, "new1").Stub?.Id);
        Assert.Equal("s2", ClaimOf(claims, "new2").Stub?.Id);
    }

    [Fact]
    public void ANewEpisode_LeavesTheStubOfTheOtherEpisodeOfItsFile()
    {
        var claims = new StubMatcher([Stub("s2", "k2", 2, null, "x")]).ClaimAll([Content("k1", 1, null, "a", "x"), Content("k2", 2, null, "x")]);

        Assert.Null(ClaimOf(claims, "k1").Stub);
        Assert.Equal("s2", ClaimOf(claims, "k2").Stub?.Id);
    }

    [Fact]
    public void AnEpisodeWithANewNumber_KeepsItsStub()
    {
        var claims = new StubMatcher([Stub("s1", "old1", 1, null, "x")]).ClaimAll([Content("new5", 5, null, "x")]);

        Assert.Equal("s1", ClaimOf(claims, "new5").Stub?.Id);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("z")]
    public void OfTwoStubsOfAKey_TheOneOfTheSameEpisodesIsClaimed(string id)
    {
        var stubs = new[] { Stub(id, "k1", 1, 2, "x"), Stub("m", "k1", 1, null, "b1"), Stub("n", "k2", 2, null, "b2") };

        var claims = new StubMatcher(stubs).ClaimAll([Content("k1", 1, null, "b1", "x"), Content("k2", 2, null, "b2", "x")]);

        Assert.Equal("m", ClaimOf(claims, "k1").Stub?.Id);
        Assert.Equal("n", ClaimOf(claims, "k2").Stub?.Id);
        Assert.All(claims, claim => Assert.Equal(id, Assert.Single(claim.Merged).Id));
    }

    // E3 is outside the file, so its stub takes nothing.
    [Fact]
    public void AStubOfTwoEpisodes_IsNotMergedIntoAGroupThatGoesPastIt()
    {
        var stubs = new[] { Stub("s", "k1", 1, 2, "x"), Stub("s1", "k1", 1, null, "b"), Stub("s2", "k2", 2, 3, "y") };

        var claims = new StubMatcher(stubs).ClaimAll([Content("k1", 1, null, "b", "x"), Content("k2", 2, 3, "y", "x")]);

        Assert.Equal("s", Assert.Single(ClaimOf(claims, "k1").Merged).Id);
        Assert.Empty(ClaimOf(claims, "k2").Merged);
    }

    private static StubClaim ClaimOf(IEnumerable<StubClaim> claims, string key) => Assert.Single(claims, claim => claim.Content.Identity.Key == key);

    private static StubEntry Stub(string id, string key, int first, int? last, params string[] items) => new()
    {
        Id = id,
        ServerId = _server,
        Kind = StubKind.Episode,
        RelativePath = id + ".strm",
        IdentityKey = key,
        SeasonNumber = 1,
        EpisodeNumber = first,
        EpisodeNumberEnd = last,
        Sources = items.Select(item => new StubSource(_server, item, item)).ToList()
    };

    private static ResolvedContent Content(string key, int first, int? last, params string[] items) => new(
        new ContentIdentity(key, "w", ContentKind.Episode, MatchKind.ProviderId, 1, first),
        false,
        new ResolvedSource(_server, "Server", ServerSyncState.Complete, items[0], items[0], null, new SourceQuality(1080, false, 1000, "h264"), DateTimeOffset.UnixEpoch),
        [])
    {
        EpisodeNumberEnd = last,
        RemoteItems = items.Select(item => new ResolvedItem(_server, item)).ToList()
    };
}
