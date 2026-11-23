using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Storage;
using Jellyfin.Plugin.JellyMesh.Tests.Storage;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class KeptWatchStateStoreTests : IDisposable
{
    private static readonly DateTime _saved = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid _anna = Guid.NewGuid();
    private static readonly Guid _bruno = Guid.NewGuid();

    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private KeptWatchStateStore Store() => new(_database);

    private static KeptTitle Film(Guid oldItem, string title = "Film", params KeptUserState[] users) => new()
    {
        OldItemId = oldItem,
        Kind = KeptTitleKind.Movie,
        Title = title,
        Year = 2020,
        SavedUtc = _saved,
        Users = users
    };

    private static KeptUserState Watched(Guid user, int count = 1) =>
        new(user, true, count, 0, new DateTime(2026, 10, 2, 20, 30, 15, DateTimeKind.Utc).AddTicks(1234567), false, null);

    // Every field, lists and users included, in a form records compare by value.
    private static string Describe(KeptTitle title) => string.Join(
        '\n',
        (title with { Genres = [], Studios = [], People = [], Artists = [], AlbumArtists = [], ProviderIds = new Dictionary<string, string>(), Users = [], Copies = [] }).ToString(),
        string.Join(',', title.Copies),
        string.Join(',', title.Genres),
        string.Join(',', title.Studios),
        string.Join(',', title.People),
        string.Join(',', title.Artists),
        string.Join(',', title.AlbumArtists),
        string.Join(',', title.ProviderIds.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)),
        string.Join('\n', title.Users.OrderBy(user => user.UserId).Select(user => user + " " + user.LastPlayedUtc?.Ticks)));

    [Fact]
    public async Task ATitleComesBackWithItsIdentityAndEveryUser()
    {
        var oldItem = Guid.NewGuid();
        var episode = new KeptTitle
        {
            OldItemId = oldItem,
            Kind = KeptTitleKind.Episode,
            Title = "L'ombra \"del\" vento; DROP TABLE Titles",
            Year = 2019,
            Ids = new ContentIds("12", "tt3", "45"),
            OriginalTitle = "Shadow",
            RunTimeTicks = 26_000_000_000,
            Overview = "Plot",
            Genres = ["Drama", "Mystery"],
            Studios = ["Rai"],
            People = ["Anna", "Bruno"],
            SeasonNumber = 2,
            EpisodeNumber = 3,
            EpisodeNumberEnd = 4,
            SavedUtc = _saved,
            Users =
            [
                new KeptUserState(_anna, false, 0, 9_000_000_000, new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), true, 7.5),
                new KeptUserState(_bruno, true, 2, 0, null, false, null)
            ]
        };

        await Store().SaveAsync([episode], CancellationToken.None);
        var read = Assert.Single(await Store().ReadAsync(CancellationToken.None));

        Assert.True(read.Id > 0);
        Assert.Equal(Describe(episode with { Id = read.Id }), Describe(read));
        Assert.All(read.Users.Select(user => user.LastPlayedUtc).OfType<DateTime>(), date => Assert.Equal(DateTimeKind.Utc, date.Kind));
        Assert.Equal(DateTimeKind.Utc, read.SavedUtc.Kind);
    }

    [Fact]
    public async Task AMusicVideoComesBackWithItsArtists()
    {
        var clip = new KeptTitle
        {
            OldItemId = Guid.NewGuid(),
            Kind = KeptTitleKind.MusicVideo,
            Title = "Song",
            Year = 2019,
            Ids = new ContentIds(null, "tt1"),
            Artists = ["Singer", "Guest"],
            SavedUtc = _saved,
            Users = [Watched(_anna)]
        };

        await Store().SaveAsync([clip], CancellationToken.None);
        var read = Assert.Single(await Store().ReadAsync(CancellationToken.None));

        Assert.Equal(KeptTitleKind.MusicVideo, read.Kind);
        Assert.Equal(["Singer", "Guest"], read.Artists);
    }

    // Row 532: each kind of its own, with everything a rule of identity may read.
    [Theory]
    [InlineData(KeptTitleKind.Track)]
    [InlineData(KeptTitleKind.AudioBook)]
    [InlineData(KeptTitleKind.Book)]
    [InlineData(KeptTitleKind.HomeVideo)]
    public async Task ATitleOfATypeOfItsOwnComesBackWithItsData(KeptTitleKind kind)
    {
        var kept = new KeptTitle
        {
            OldItemId = Guid.NewGuid(),
            Kind = kind,
            Title = "Song",
            Year = 2019,
            RunTimeTicks = 1_800_000_000,
            Artists = ["Singer", "Guest"],
            AlbumArtists = ["Band \"One\"", "Band, Two"],
            Album = "L'album",
            SeriesName = "Saga",
            ProviderIds = new Dictionary<string, string> { ["MusicBrainzRecording"] = "rec-1", ["ISBN"] = "978" },
            Place = "Band/Album/01 - Song.strm",
            SeasonNumber = 2,
            EpisodeNumber = 3,
            SavedUtc = _saved,
            Users = [Watched(_anna)]
        };

        await Store().SaveAsync([kept], CancellationToken.None);
        var read = Assert.Single(await Store().ReadAsync(CancellationToken.None));

        Assert.Equal(Describe(kept with { Id = read.Id }), Describe(read));
    }

    [Fact]
    public async Task SavingTheSameOldItemAgainReplacesItsRecord()
    {
        var oldItem = Guid.NewGuid();
        var store = Store();
        await store.SaveAsync([Film(oldItem, "Film", Watched(_anna), Watched(_bruno))], CancellationToken.None);

        await store.SaveAsync([Film(oldItem, "Film", Watched(_anna, 3))], CancellationToken.None);

        var read = Assert.Single(await store.ReadAsync(CancellationToken.None));
        Assert.Equal(3, Assert.Single(read.Users).PlayCount);
    }

    [Fact]
    public async Task SavingTheSameOldItemAgainKeepsItsFirstSavedDate()
    {
        var oldItem = Guid.NewGuid();
        var store = Store();
        await store.SaveAsync([Film(oldItem, "Film", Watched(_anna))], CancellationToken.None);

        await store.SaveAsync([Film(oldItem, "Film", Watched(_anna, 3)) with { SavedUtc = _saved.AddDays(30) }], CancellationToken.None);

        var read = Assert.Single(await store.ReadAsync(CancellationToken.None));
        Assert.Equal(3, Assert.Single(read.Users).PlayCount);
        Assert.Equal(_saved, read.SavedUtc);
        Assert.Equal(1, await store.PruneAsync(_saved.AddDays(1), new HashSet<Guid> { _anna }, CancellationToken.None));
        Assert.Empty(await store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ForgettingTheLastUserOfATitleDeletesTheTitle()
    {
        var store = Store();
        await store.SaveAsync([Film(Guid.NewGuid(), "One", Watched(_anna), Watched(_bruno)), Film(Guid.NewGuid(), "Two", Watched(_anna))], CancellationToken.None);
        var titles = await store.ReadAsync(CancellationToken.None);
        var one = titles.Single(title => title.Title == "One");
        var two = titles.Single(title => title.Title == "Two");

        await store.ForgetAsync([(one.Id, _anna), (two.Id, _anna)], CancellationToken.None);

        var left = Assert.Single(await store.ReadAsync(CancellationToken.None));
        Assert.Equal("One", left.Title);
        Assert.Equal(_bruno, Assert.Single(left.Users).UserId);
    }

    [Fact]
    public async Task PruningDeletesOldTitlesAndTheRowsOfUsersWhoAreGone()
    {
        var store = Store();
        await store.SaveAsync(
            [
                Film(Guid.NewGuid(), "Old", Watched(_anna)) with { SavedUtc = _saved.AddDays(-91) },
                Film(Guid.NewGuid(), "Recent", Watched(_anna), Watched(_bruno)),
                Film(Guid.NewGuid(), "Bruno only", Watched(_bruno))
            ],
            CancellationToken.None);

        var pruned = await store.PruneAsync(_saved.AddDays(-90), new HashSet<Guid> { _anna }, CancellationToken.None);

        var left = Assert.Single(await store.ReadAsync(CancellationToken.None));
        Assert.Equal("Recent", left.Title);
        Assert.Equal(_anna, Assert.Single(left.Users).UserId);
        Assert.Equal(2, pruned);
    }

    [Fact]
    public async Task NoUsersKnownKeepsEveryUserRow()
    {
        var store = Store();
        await store.SaveAsync([Film(Guid.NewGuid(), "Film", Watched(_anna))], CancellationToken.None);

        await store.PruneAsync(_saved.AddDays(-90), new HashSet<Guid>(), CancellationToken.None);

        Assert.Single(Assert.Single(await store.ReadAsync(CancellationToken.None)).Users);
    }

    [Fact]
    public async Task NothingKeptMeansNoTitles()
    {
        Assert.False(await Store().HasTitlesAsync(CancellationToken.None));

        await Store().SaveAsync([Film(Guid.NewGuid(), "Film", Watched(_anna))], CancellationToken.None);

        Assert.True(await Store().HasTitlesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ATitleIsKeptInTheTablesOfThePluginDatabase()
    {
        await Store().SaveAsync([Film(Guid.NewGuid(), "Film", Watched(_anna), Watched(_bruno))], CancellationToken.None);

        Assert.Equal(1, _database.Count("kept_titles"));
        Assert.Equal(2, _database.Count("kept_user_states"));
        Assert.Equal(1, _database.Count("kept_titles WHERE kind = 'Movie'"));
    }

    [Fact]
    public async Task WhileTheDatabaseIsSuspendedNothingIsReadOrWritten()
    {
        _database.Suspended = true;

        await Assert.ThrowsAsync<StoreSuspendedException>(() => Store().SaveAsync([Film(Guid.NewGuid(), "Film", Watched(_anna))], CancellationToken.None));
        await Assert.ThrowsAsync<StoreSuspendedException>(() => Store().ReadAsync(CancellationToken.None));

        Assert.Equal(0, _database.Count("kept_titles"));
    }

    private static readonly MergedCopy _alfa = new() { ServerId = "a", ItemId = "x", Tmdb = "1", Title = "Alfa", Year = 2026 };
    private static readonly MergedCopy _sonda = new() { ServerId = "c", ItemId = "y" };

    // D33: a title keeps the copies of its manual merges, which go with it.
    [Fact]
    public async Task ATitleKeepsTheCopiesOfItsManualMergesUntilItGoes()
    {
        var store = Store();
        var oldItem = Guid.NewGuid();
        await store.SaveAsync([Film(oldItem, "Sonda", Watched(_anna)) with { Copies = [_alfa, _sonda] }], CancellationToken.None);
        await store.SaveAsync([Film(oldItem, "Sonda", Watched(_anna)) with { Copies = [_sonda, _alfa] }], CancellationToken.None);

        var title = Assert.Single(await Store().ReadAsync(CancellationToken.None));

        Assert.Equal([_sonda, _alfa], title.Copies);
        Assert.Equal(2, _database.Count("kept_title_copies"));
        await store.ForgetAsync([(title.Id, _anna)], CancellationToken.None);
        Assert.Equal(0, _database.Count("kept_title_copies"));
    }

    // The merges a removed server had, saved before its copies leave them, stay for its titles until they are old.
    [Fact]
    public async Task TheMergesOfARemovedServerStayUntilTheyAreOld()
    {
        var store = Store();
        var server = Guid.NewGuid();
        var merge = new ManualMerge { Id = Guid.NewGuid(), CreatedUtc = _saved, Copies = [_alfa, _sonda] };
        await store.SaveMergesAsync(server, [merge with { Copies = [_alfa] }], _saved, CancellationToken.None);
        await store.SaveMergesAsync(server, [merge], _saved, CancellationToken.None);

        var (removed, read) = Assert.Single(await Store().ReadMergesAsync(CancellationToken.None));

        Assert.Equal(server, removed);
        Assert.Equal(merge.Id, read.Id);
        Assert.Equal([_alfa, _sonda], read.Copies);
        await store.PruneAsync(_saved.AddDays(-90), new HashSet<Guid>(), CancellationToken.None);
        Assert.Single(await store.ReadMergesAsync(CancellationToken.None));
        await store.PruneAsync(_saved.AddDays(1), new HashSet<Guid>(), CancellationToken.None);
        Assert.Empty(await store.ReadMergesAsync(CancellationToken.None));
    }

}
