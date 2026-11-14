using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class KeptWatchStateStoreTests : IDisposable
{
    private static readonly DateTime _saved = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid _anna = Guid.NewGuid();
    private static readonly Guid _bruno = Guid.NewGuid();

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jellymesh-kept-" + Guid.NewGuid().ToString("N"));

    public KeptWatchStateStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private KeptWatchStateStore Store() => new(_folder, new CapturingLogger<KeptWatchStateStore>());

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
        (title with { Genres = [], Studios = [], People = [], Users = [] }).ToString(),
        string.Join(',', title.Genres),
        string.Join(',', title.Studios),
        string.Join(',', title.People),
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
    public async Task ADamagedFileIsSetAsideAndANewOneMade()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder, KeptWatchStateStore.FileName), new string('x', 4096));

        var store = Store();
        await store.SaveAsync([Film(Guid.NewGuid(), "Film", Watched(_anna))], CancellationToken.None);

        Assert.Single(await store.ReadAsync(CancellationToken.None));
        Assert.Single(Directory.GetFiles(_folder, KeptWatchStateStore.FileName + ".damaged-*"));
    }

    [Fact]
    public async Task TheFileIsInTheGivenFolder()
    {
        await Store().SaveAsync([Film(Guid.NewGuid(), "Film", Watched(_anna))], CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_folder, KeptWatchStateStore.FileName)));
    }
}
