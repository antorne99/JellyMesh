using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

public class ContentIdsTests
{
    [Fact]
    public void FromProviderIds_ReadsJellyfinKeys()
    {
        var ids = ContentIds.FromProviderIds(new Dictionary<string, string>
        {
            ["Tmdb"] = "999000101",
            ["Tvdb"] = "999000102",
            ["Imdb"] = "tt99000101",
            ["TvRage"] = "1",
        });

        Assert.Equal(new ContentIds("999000101", "tt99000101", "999000102"), ids);
    }

    [Fact]
    public void FromProviderIds_IgnoresKeyCase()
    {
        var ids = ContentIds.FromProviderIds(new Dictionary<string, string> { ["tmdb"] = "1", ["IMDB"] = "tt1" });

        Assert.Equal(new ContentIds("1", "tt1"), ids);
    }

    [Fact]
    public void FromProviderIds_HandlesNoIds()
    {
        Assert.Equal(ContentIds.None, ContentIds.FromProviderIds(null));
        Assert.Equal(ContentIds.None, ContentIds.FromProviderIds(new Dictionary<string, string>()));
    }
}
