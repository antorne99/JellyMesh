using System;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The item type each kind of stub becomes, by which Jellyfin names its ID.
public sealed class StubItemTypesTests
{
    [Theory]
    [InlineData(StubKind.Movie, typeof(Movie))]
    [InlineData(StubKind.Episode, typeof(Episode))]
    [InlineData(StubKind.Track, typeof(Audio))]
    [InlineData(StubKind.MusicVideo, typeof(MusicVideo))]
    [InlineData(StubKind.HomeVideo, typeof(Video))]
    [InlineData(StubKind.Book, typeof(Book))]
    public void EachKindHasTheTypeOfItsItem(StubKind kind, Type type) => Assert.Equal(type, StubItemTypes.Of(kind));

    [Fact]
    public void EveryKindHasAType() => Assert.All(Enum.GetValues<StubKind>(), kind => Assert.NotNull(StubItemTypes.Of(kind)));
}
