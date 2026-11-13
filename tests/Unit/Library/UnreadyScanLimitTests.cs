using System;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class UnreadyScanLimitTests
{
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_library_gets_one_scan_a_day()
    {
        var limit = new UnreadyScanLimit(_clock);

        Assert.True(limit.TryClaim(["Film di casa"]));
        _clock.Now += UnreadyScanLimit.Interval - TimeSpan.FromSeconds(1);
        Assert.False(limit.TryClaim(["Film di casa"]));
        _clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(limit.TryClaim(["Film di casa"]));
    }

    [Fact]
    public void The_name_is_compared_without_case_like_the_library_id()
    {
        var limit = new UnreadyScanLimit(_clock);

        Assert.True(limit.TryClaim(["Film di casa"]));

        Assert.False(limit.TryClaim(["FILM DI CASA"]));
    }

    [Fact]
    public void Another_library_gets_its_scan_and_the_scan_counts_for_every_library_without_an_id()
    {
        var limit = new UnreadyScanLimit(_clock);
        Assert.True(limit.TryClaim(["Film di casa"]));
        _clock.Now += TimeSpan.FromHours(20);

        Assert.True(limit.TryClaim(["Film di casa", "Serie di casa"]));
        _clock.Now += TimeSpan.FromHours(20);

        Assert.False(limit.TryClaim(["Film di casa"]));
    }

    [Fact]
    public void A_library_whose_id_came_back_keeps_its_day()
    {
        var limit = new UnreadyScanLimit(_clock);
        Assert.True(limit.TryClaim(["Film di casa"]));
        _clock.Now += TimeSpan.FromHours(1);
        Assert.True(limit.TryClaim(["Serie di casa"]));

        Assert.False(limit.TryClaim(["Film di casa"]));
    }

    [Fact]
    public void No_library_without_an_id_claims_nothing()
    {
        var limit = new UnreadyScanLimit(_clock);

        Assert.False(limit.TryClaim([]));
    }
}
