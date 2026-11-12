using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

public sealed class RemoteCatalogReaderTests : IDisposable
{
    private readonly SyncFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private Task<RemoteResult<System.Collections.Generic.IReadOnlyList<IndexedItem>>> ReadAsync(RemoteServerInfo server)
        => _fixture.Reader.ReadAsync(server, SyncFixture.Start, CancellationToken.None);

    // By default every film of the fake collections is in the catalog.
    private Task<RemoteResult<System.Collections.Generic.IReadOnlyDictionary<string, IndexedBoxSet>>> ReadCollectionsAsync(RemoteServerInfo server, params string[] filmIds)
        => _fixture.Reader.ReadCollectionsAsync(
            server,
            ["films"],
            (filmIds.Length > 0 ? filmIds : _fixture.Client.Collections.SelectMany(entry => entry.Films)).ToHashSet(StringComparer.Ordinal),
            CancellationToken.None);

    [Fact]
    public async Task EachFilmOfACollectionGetsItsNameAndTmdbId()
    {
        _fixture.Client.Collections.Add(("films", new RemoteItem { Id = "box", Name = "Saga", ProviderIds = new System.Collections.Generic.Dictionary<string, string?> { ["Tmdb"] = "10" } }, ["m1", "m2"]));
        _fixture.Client.AddCollection("other", "elsewhere", "Not read", "m9");
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadCollectionsAsync(server);

        Assert.Equal(new IndexedBoxSet("Saga", "10"), result.Value!["m1"]);
        Assert.Equal(new IndexedBoxSet("Saga", "10"), result.Value["m2"]);
        Assert.Equal(2, result.Value.Count);
    }

    [Theory]
    [InlineData("12a")]
    [InlineData("")]
    [InlineData("1\" tmdb=\"2")]
    public async Task ATmdbIdThatIsNotDigitsIsLeftOut(string tmdb)
    {
        _fixture.Client.Collections.Add(("films", new RemoteItem { Id = "box", Name = "Saga", ProviderIds = new System.Collections.Generic.Dictionary<string, string?> { ["Tmdb"] = tmdb } }, ["m1"]));
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadCollectionsAsync(server);

        Assert.Equal(new IndexedBoxSet("Saga", null), result.Value!["m1"]);
    }

    [Fact]
    public async Task AFilmInTwoCollectionsGetsTheFirstByName()
    {
        _fixture.Client.AddCollection("films", "b", "Saga B", "m1");
        _fixture.Client.AddCollection("films", "a", "Saga A", "m1");
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadCollectionsAsync(server);

        Assert.Equal("Saga A", result.Value!["m1"].Name);
    }

    [Fact]
    public async Task AFilmInTwoCollectionsGetsTheFirstByTrimmedName()
    {
        _fixture.Client.AddCollection("films", "a", "  Saga B", "m1");
        _fixture.Client.AddCollection("films", "b", "Saga A ", "m1");
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadCollectionsAsync(server);

        Assert.Equal("Saga A", result.Value!["m1"].Name);
    }

    [Fact]
    public async Task ACollectionWithoutANameIsLeftOut()
    {
        _fixture.Client.AddCollection("films", "a", " ", "m1");
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadCollectionsAsync(server);

        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task ACollectionWithANameLongerThan256CharactersIsLeftOut()
    {
        _fixture.Client.AddCollection("films", "long", new string('x', 257), "m1");
        _fixture.Client.AddCollection("films", "edge", " " + new string('y', 256) + " ", "m2");
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadCollectionsAsync(server);

        Assert.False(result.Value!.ContainsKey("m1"));
        Assert.Equal(new string('y', 256), result.Value["m2"].Name);
    }

    [Fact]
    public async Task AMemberThatIsNotAFilmOfTheCatalogIsLeftOut()
    {
        _fixture.Client.AddCollection("films", "box", "Saga", "m1", "ghost1", "ghost2");
        _fixture.Client.AddCollection("films", "other", "Zeta", "ghost3");
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadCollectionsAsync(server, "m1");

        Assert.Equal(["m1"], result.Value!.Keys);
    }

    [Fact]
    public async Task NoMoreCollectionsAreReadOnceEveryFilmOfTheCatalogHasOne()
    {
        _fixture.Client.AddCollection("films", "a", "Saga A", "m1");
        _fixture.Client.AddCollection("films", "b", "Saga B", "m1");
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadCollectionsAsync(server, "m1");

        Assert.Equal("Saga A", result.Value!["m1"].Name);
        Assert.Contains("a@0", _fixture.Client.CollectionReads);
        Assert.DoesNotContain("b@0", _fixture.Client.CollectionReads);
    }

    [Fact]
    public async Task CollectionsAndTheirFilmsAreReadPageByPage()
    {
        for (var index = 0; index < RemoteCatalogReader.PageSize + 1; index++)
        {
            _fixture.Client.AddCollection("films", "box" + index, "Saga " + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), "m" + index);
        }

        _fixture.Client.Collections[0].Films.AddRange(Enumerable.Range(1000, RemoteCatalogReader.PageSize).Select(index => "m" + index));
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadCollectionsAsync(server);

        Assert.Equal((2 * RemoteCatalogReader.PageSize) + 1, result.Value!.Count);
        Assert.Contains("films@" + RemoteCatalogReader.PageSize, _fixture.Client.CollectionReads);
        Assert.Contains("box0@" + RemoteCatalogReader.PageSize, _fixture.Client.CollectionReads);
    }

    [Fact]
    public async Task AFailedCollectionReadIsAFailure()
    {
        _fixture.Client.AddCollection("films", "a", "Saga", "m1");
        _fixture.Client.CollectionFailure = RemoteFailure.Unreachable;
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadCollectionsAsync(server);

        Assert.Equal(RemoteFailure.Unreachable, result.Failure);
    }

    [Fact]
    public async Task ReadsEveryPageInOrder()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies((2 * RemoteCatalogReader.PageSize) + 3));
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadAsync(server);

        Assert.Equal((2 * RemoteCatalogReader.PageSize) + 3, result.Value!.Count);
        Assert.Equal([0, RemoteCatalogReader.PageSize, 2 * RemoteCatalogReader.PageSize], _fixture.Client.Queries.Select(query => query.StartIndex));
        Assert.All(_fixture.Client.Queries, query => Assert.Equal(RemoteCatalogReader.PageSize, query.Limit));
    }

    [Fact]
    public async Task AnEmptyLibraryIsOneRequest()
    {
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadAsync(server);

        Assert.Empty(result.Value!);
        Assert.Single(_fixture.Client.Queries);
    }

    [Fact]
    public async Task ATooLargePageIsAskedAgainWithALowerLimit()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(70));
        _fixture.Client.OnPage = (query, _) => query.Limit > 50 ? RemoteFailure.ResponseTooLarge : RemoteFailure.None;
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadAsync(server);

        Assert.Equal(70, result.Value!.Count);
        Assert.Equal([200, 100, 50, 50], _fixture.Client.Queries.Select(query => query.Limit));
        Assert.Equal([0, 0, 0, 50], _fixture.Client.Queries.Select(query => query.StartIndex));
    }

    [Fact]
    public async Task ASingleItemOverTheLimitFailsTheRead()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(1));
        _fixture.Client.OnPage = (_, _) => RemoteFailure.ResponseTooLarge;
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadAsync(server);

        Assert.Equal(RemoteFailure.ResponseTooLarge, result.Failure);
        Assert.Equal(1, _fixture.Client.Queries[^1].Limit);
    }

    [Fact]
    public async Task AnItemRemovedDuringTheReadRestartsIt()
    {
        var movies = TestItems.Movies(RemoteCatalogReader.PageSize + 10);
        _fixture.Client.Add("films", RemoteItemType.Movie, movies);
        _fixture.Client.OnPage = (_, call) =>
        {
            if (call == 2)
            {
                _fixture.Client.Items[("films", RemoteItemType.Movie)].RemoveAt(0);
            }

            return RemoteFailure.None;
        };
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadAsync(server);

        // The removal shifts the item at the page boundary into the page already read: the count alone would still match.
        Assert.Equal(movies.Skip(1).Select(movie => movie.Id).Order(), result.Value!.Select(item => item.ItemId).Order());
    }

    [Fact]
    public async Task ACatalogThatNeverHoldsStillFailsInsteadOfGivingAPartialRead()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(RemoteCatalogReader.PageSize + 10));
        var total = 0;
        _fixture.Client.Rewrite = (_, page) => page with { TotalRecordCount = page.TotalRecordCount + total++ };
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadAsync(server);

        Assert.Equal(RemoteFailure.CatalogChanged, result.Failure);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task AnItemSeenTwiceAcrossPagesCountsOnceAndFailsTheCount()
    {
        var movies = TestItems.Movies(RemoteCatalogReader.PageSize + 1);
        _fixture.Client.Add("films", RemoteItemType.Movie, movies);
        _fixture.Client.Rewrite = (query, page) => query.StartIndex == RemoteCatalogReader.PageSize
            ? page with { Items = [movies[0]] }
            : page;
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadAsync(server);

        Assert.Equal(RemoteFailure.CatalogChanged, result.Failure);
    }

    [Fact]
    public async Task AnEmptyPageBeforeTheTotalStopsTheRead()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(1));
        _fixture.Client.Rewrite = (_, page) => new RemoteItemPage([], 5);
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadAsync(server);

        Assert.Equal(RemoteFailure.CatalogChanged, result.Failure);
        Assert.Equal(3, _fixture.Client.Queries.Count);
    }

    [Fact]
    public async Task AnAbsurdTotalIsRefused()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(1));
        _fixture.Client.Rewrite = (_, page) => page with { TotalRecordCount = RemoteCatalogReader.MaxItemsPerQuery + 1 };
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadAsync(server);

        Assert.Equal(RemoteFailure.InvalidResponse, result.Failure);
    }

    [Fact]
    public async Task ASeriesLibraryReadsSeriesSeasonsAndEpisodes()
    {
        _fixture.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Client.Add("shows", RemoteItemType.Season, TestItems.Season("s1", "s", 1));
        _fixture.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e1", "s", 1, 1));
        var server = await _fixture.AddServerAsync("A", "shows");

        var result = await ReadAsync(server);

        Assert.Equal(
            [IndexedItemKind.Series, IndexedItemKind.Season, IndexedItemKind.Episode],
            result.Value!.Select(item => item.Kind));
        Assert.Equal(
            [RemoteItemType.Series, RemoteItemType.Season, RemoteItemType.Episode],
            _fixture.Client.Queries.Select(query => query.Type));
    }

    [Fact]
    public async Task ASeriesRowThatItemsCollapsesIsLookedUpById()
    {
        _fixture.Client.Add("shows", RemoteItemType.Series, TestItems.Series("listed"));
        _fixture.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e1", "collapsed", 1, 1));
        _fixture.Client.ItemsById.Add(TestItems.Series("collapsed"));
        var server = await _fixture.AddServerAsync("A", "shows");

        var result = await ReadAsync(server);

        var series = result.Value!.Single(item => item.ItemId == "collapsed");
        Assert.Equal(IndexedItemKind.Series, series.Kind);
        Assert.Equal("shows", series.LibraryId);
        Assert.Equal("71", series.ProviderIds["Tvdb"]);
    }

    [Fact]
    public async Task ALookupAnsweredByTheRepresentativeRowIsKeptUnderTheRowAskedFor()
    {
        _fixture.Client.Add("shows", RemoteItemType.Series, TestItems.Series("listed"));
        _fixture.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e1", "hidden", 1, 1));
        _fixture.Client.ItemsById.Add(TestItems.Series("listed"));
        _fixture.Client.RepresentedBy["hidden"] = "listed";
        var server = await _fixture.AddServerAsync("Friend", "shows");

        var result = await ReadAsync(server);

        var series = result.Value!.Single(item => item.ItemId == "hidden");
        Assert.Equal("71", series.ProviderIds["Tvdb"]);
        Assert.DoesNotContain(_fixture.ReaderLogger.Lines, line => line.Contains("does not list", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEpisodeWhoseSeriesCannotBeFoundIsKeptAndLogged()
    {
        _fixture.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e1", "gone", 1, 1));
        var server = await _fixture.AddServerAsync("Friend", "shows");

        var result = await ReadAsync(server);

        Assert.Equal("e1", Assert.Single(result.Value!).ItemId);
        Assert.Contains(_fixture.ReaderLogger.Lines, line => line.Contains("Friend", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AChosenLibraryThatIsGoneFailsTheRead()
    {
        var server = await _fixture.AddServerAsync("A", "films", "deleted");

        var result = await ReadAsync(server);

        Assert.Equal(RemoteFailure.LibraryUnavailable, result.Failure);
        Assert.Empty(_fixture.Client.Queries);
    }

    [Fact]
    public async Task OnlyChosenLibrariesAreRead()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        _fixture.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadAsync(server);

        Assert.Equal("m", Assert.Single(result.Value!).ItemId);
    }

    [Fact]
    public async Task NoChosenLibraryMeansNoRequest()
    {
        var server = await _fixture.AddServerAsync("A");

        var result = await ReadAsync(server);

        Assert.Empty(result.Value!);
        Assert.Empty(_fixture.Client.Queries);
    }

    [Fact]
    public async Task ARefusedTokenIsRenewedInTheMiddleOfARead()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(RemoteCatalogReader.PageSize + 1));
        _fixture.Client.OnPage = (_, call) => call == 2 ? RemoteFailure.AuthenticationFailed : RemoteFailure.None;
        var server = await _fixture.AddServerAsync("A", "films");
        var logins = _fixture.Client.Logins;

        var result = await ReadAsync(server);

        Assert.Equal(RemoteCatalogReader.PageSize + 1, result.Value!.Count);
        Assert.Equal(logins + 1, _fixture.Client.Logins);
    }

    [Fact]
    public async Task ALostKeyStopsTheReadBeforeAnyRequest()
    {
        var server = await _fixture.AddServerAsync("A", "films");
        _fixture.KeySource.Key = null;

        var result = await ReadAsync(server);

        Assert.Equal(RemoteFailure.Suspended, result.Failure);
        Assert.Empty(_fixture.Client.Queries);
    }

    [Fact]
    public async Task ACatalogHeavierThanTheBudgetIsRefusedHoweverSmallItsPages()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(RemoteCatalogReader.PageSize * 3));
        _fixture.Client.Rewrite = (_, page) => page with { ByteCount = (RemoteCatalogReader.MaxBytesPerRead / 2) + 1 };
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadAsync(server);

        Assert.Equal(RemoteFailure.ResponseTooLarge, result.Failure);
        Assert.Equal(2, _fixture.Client.Queries.Count);
    }

    [Fact]
    public async Task ACatalogUnderTheBudgetReadsNormally()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(RemoteCatalogReader.PageSize * 2));
        _fixture.Client.Rewrite = (_, page) => page with { ByteCount = RemoteCatalogReader.MaxBytesPerRead / 2 };
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadAsync(server);

        Assert.Equal(RemoteCatalogReader.PageSize * 2, result.Value!.Count);
    }

    [Fact]
    public async Task ASecondAttemptDoesNotPayForTheFirst()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(3));
        var reads = 0;
        _fixture.Client.Rewrite = (_, page) =>
        {
            var weighed = page with { ByteCount = (RemoteCatalogReader.MaxBytesPerRead * 3) / 4 };
            return ++reads == 1 ? weighed with { Items = [] } : weighed;
        };
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await ReadAsync(server);

        Assert.Equal(3, result.Value!.Count);
    }

    [Fact]
    public async Task AFeedThatNeverEndsIsRefusedAndHasNoMoreThanItsPages()
    {
        var change = new RemoteChange("item", "Movie", ["films"]);
        var cursor = 0;
        _fixture.Client.Feed = _ => RemoteResult.Success(new RemoteChangePage(false, "cursor-" + ++cursor, true, [change]));
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await _fixture.Reader.ReadFeedAsync(server, "start", new System.Collections.Generic.HashSet<string>(), SyncFixture.Start, CancellationToken.None);

        Assert.Equal(RemoteFailure.InvalidResponse, result.Failure);
        Assert.Equal(RemoteCatalogReader.MaxFeedPages + 1, _fixture.Client.FeedReads.Count);
    }

    [Fact]
    public async Task AFeedHeavierThanTheBudgetIsRefused()
    {
        var cursor = 0;
        _fixture.Client.Feed = _ => RemoteResult.Success(new RemoteChangePage(false, "cursor-" + ++cursor, true, []) { ByteCount = RemoteCatalogReader.MaxBytesPerRead / 2 });
        var server = await _fixture.AddServerAsync("A", "films");

        var result = await _fixture.Reader.ReadFeedAsync(server, "start", new System.Collections.Generic.HashSet<string>(), SyncFixture.Start, CancellationToken.None);

        Assert.Equal(RemoteFailure.ResponseTooLarge, result.Failure);
        Assert.Equal(3, _fixture.Client.FeedReads.Count);
    }
}
