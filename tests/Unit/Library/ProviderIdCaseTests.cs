using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// A remote may send provider ids that differ only by case; Jellyfin keys them ignoring case, so one is kept, always the same.
public class ProviderIdCaseTests
{
    private static StubMetadata Metadata() => new()
    {
        IsProtected = true,
        Title = "Song",
        ProviderIds = new Dictionary<string, string>(StringComparer.Ordinal) { ["tmdb"] = "2", ["Tmdb"] = "1", ["Imdb"] = "tt1" }
    };

    [Fact]
    public async Task AFilmKeepsTheFirstIdOfKeysThatDifferOnlyByCase()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Path = "/federata/m.strm" };

        await new StubItemUpdater(new Mock<ILibraryManager>().Object).ApplyMetadataAsync(movie, Metadata(), CancellationToken.None);

        Assert.Equal("1", movie.ProviderIds["tmdb"]);
        Assert.Equal("tt1", movie.ProviderIds["Imdb"]);
    }

    [Fact]
    public void ATrackKeepsTheFirstIdAndIsNotRewrittenTheNextTime()
    {
        var track = new Audio { Id = Guid.NewGuid(), Path = "/federata/t.strm" };

        StubItemUpdater.WriteTrack(track, Metadata());

        Assert.Equal("1", track.ProviderIds["TMDB"]);
        Assert.False(StubItemUpdater.WriteTrack(track, Metadata()));
    }
}
