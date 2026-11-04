using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Index;

public sealed class IndexFileTests : IDisposable
{
    private static readonly DateTimeOffset _now = new(2026, 11, 4, 3, 0, 0, TimeSpan.Zero);
    private static readonly Guid _serverId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jellymesh-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingLogger<IndexFile> _logger = new();

    public IndexFileTests() => Directory.CreateDirectory(_folder);

    private string MainPath => Path.Combine(_folder, IndexFile.FileName);

    private string BackupPath => MainPath + ".bak";

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private IndexFile NewFile() => new(_folder, _logger);

    [Fact]
    public void AFileWrittenBeforeTheMarkExistedStillLoads()
    {
        File.WriteAllText(MainPath, "{\"Version\":1,\"Health\":\"Ok\",\"Servers\":[{\"ServerId\":\"" + _serverId + "\",\"State\":\"Complete\",\"Items\":[]}]}");

        var snapshot = NewFile().Load();

        Assert.Equal(IndexHealth.Ok, snapshot.Health);
        Assert.Null(snapshot.Find(_serverId)!.Mark);
    }

    private static IndexedItem FullItem() => new()
    {
        ItemId = "item",
        Kind = IndexedItemKind.Episode,
        LibraryId = "library",
        LastSeenUtc = _now,
        Name = "Episode",
        OriginalTitle = "Original",
        Overview = "Plot",
        Taglines = ["Tagline"],
        Genres = ["Drama"],
        Studios = ["Studio"],
        Tags = ["Tag"],
        People = [new IndexedPerson("Name", "Role", "Actor")],
        OfficialRating = "PG",
        CommunityRating = 7.5f,
        CriticRating = 80f,
        PremiereDate = _now,
        EndDate = _now,
        ProductionYear = 2026,
        Status = "Continuing",
        ProviderIds = new System.Collections.Generic.Dictionary<string, string> { ["Tvdb"] = "1", ["Custom"] = "2" },
        RunTimeTicks = 600_000_000,
        DateCreated = _now,
        Etag = "etag",
        IndexNumber = 2,
        ParentIndexNumber = 1,
        SeriesId = "series",
        SeriesName = "Show",
        SeasonId = "season",
        SeasonName = "Season 1",
        ImageTags = new System.Collections.Generic.Dictionary<string, string> { ["Primary"] = "tag" },
        BackdropImageTags = ["backdrop"],
        Versions =
        [
            new IndexedVersion
            {
                MediaSourceId = "source",
                Name = "1080p",
                Container = "mkv",
                Size = 123,
                ContainerBitRate = 456,
                RunTimeTicks = 789,
                ETag = "source-etag",
                Protocol = "File",
                IsRemote = false,
                Height = 1080,
                Width = 1920,
                Codec = "hevc",
                BitRate = 5_000_000,
                VideoRange = "HDR",
                VideoRangeType = "HDR10"
            }
        ]
    };

    private static IndexSnapshot Snapshot(params ServerCatalog[] servers)
        => IndexSnapshot.Empty(IndexHealth.Ok) with { Servers = servers };

    private static ServerCatalog Server(ServerSyncState state = ServerSyncState.Complete) => new()
    {
        ServerId = _serverId,
        State = state,
        LastFailure = state == ServerSyncState.Stale ? RemoteFailure.Unreachable : null,
        LastAttemptUtc = _now,
        LastSuccessUtc = _now,
        Items = [FullItem()]
    };

    private async Task WriteGoodFileAsync(params ServerCatalog[] servers)
        => await NewFile().SaveAsync(Snapshot(servers), keepCurrentAsBackup: true, CancellationToken.None);

    [Fact]
    public void WithNoFileTheIndexIsEmptyAndSound()
    {
        var snapshot = NewFile().Load();

        Assert.Equal(IndexHealth.Ok, snapshot.Health);
        Assert.Empty(snapshot.Servers);
    }

    [Fact]
    public async Task EverythingSurvivesARoundTrip()
    {
        await WriteGoodFileAsync(Server());

        var loaded = NewFile().Load();

        var server = Assert.Single(loaded.Servers);
        Assert.Equal(_serverId, server.ServerId);
        Assert.Equal(ServerSyncState.Complete, server.State);
        Assert.Equal(_now, server.LastSuccessUtc);
        var expected = FullItem();
        var item = Assert.Single(server.Items);
        Assert.Equal(expected.Name, item.Name);
        Assert.Equal(expected.Taglines, item.Taglines);
        Assert.Equal(expected.Genres, item.Genres);
        Assert.Equal(expected.Studios, item.Studios);
        Assert.Equal(expected.Tags, item.Tags);
        Assert.Equal(expected.People, item.People);
        Assert.Equal(expected.ProviderIds, item.ProviderIds);
        Assert.Equal(expected.ImageTags, item.ImageTags);
        Assert.Equal(expected.BackdropImageTags, item.BackdropImageTags);
        Assert.Equal(expected.Versions, item.Versions);
        Assert.Equal(expected with
        {
            Taglines = item.Taglines,
            Genres = item.Genres,
            Studios = item.Studios,
            Tags = item.Tags,
            People = item.People,
            ProviderIds = item.ProviderIds,
            ImageTags = item.ImageTags,
            BackdropImageTags = item.BackdropImageTags,
            Versions = item.Versions
        }, item);
    }

    [Fact]
    public async Task AFailureAndItsStateSurviveARoundTrip()
    {
        await WriteGoodFileAsync(Server(ServerSyncState.Stale));

        var server = Assert.Single(NewFile().Load().Servers);

        Assert.Equal(ServerSyncState.Stale, server.State);
        Assert.Equal(RemoteFailure.Unreachable, server.LastFailure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":1,\"Health\":\"Ok\"}")]
    [InlineData("{\"Version\":1,\"Health\":\"Ok\",\"Servers\":null}")]
    [InlineData("{\"Version\":99,\"Health\":\"Ok\",\"Servers\":[]}")]
    [InlineData("{\"Version\":1,\"Health\":\"Ok\",\"Servers\":[{\"ServerId\":\"11111111-2222-3333-4444-555555555555\",\"State\":\"Complete\",\"Items\":[{}]}]}")]
    [InlineData("{\"Version\":1,\"Health\":\"Ok\",\"Servers\":[{\"ServerId\":\"11111111-2222-3333-4444-555555555555\",\"State\":\"Complete\",\"Items\":null}]}")]
    public void ADamagedFileWithNoBackupIsReportedAndNotTakenForAnEmptyIndex(string content)
    {
        File.WriteAllText(MainPath, content);

        var snapshot = NewFile().Load();

        Assert.Equal(IndexHealth.Corrupt, snapshot.Health);
        Assert.Empty(snapshot.Servers);
        Assert.False(snapshot.IsAuthoritativeFor(_serverId));
        Assert.Equal(content, File.ReadAllText(MainPath + ".damaged"));
    }

    [Fact]
    public async Task ATruncatedFileIsDamaged()
    {
        await WriteGoodFileAsync(Server());
        var bytes = await File.ReadAllBytesAsync(MainPath);
        await File.WriteAllBytesAsync(MainPath, bytes[..(bytes.Length / 2)]);

        Assert.Equal(IndexHealth.Corrupt, NewFile().Load().Health);
    }

    [Fact]
    public async Task ADamagedFileFallsBackToTheBackupAndNoServerIsAuthoritative()
    {
        await WriteGoodFileAsync(Server());
        await WriteGoodFileAsync(Server());
        Assert.True(File.Exists(BackupPath));
        await File.WriteAllTextAsync(MainPath, "damaged");

        var snapshot = NewFile().Load();

        Assert.Equal(IndexHealth.RestoredFromBackup, snapshot.Health);
        var server = Assert.Single(snapshot.Servers);
        Assert.Single(server.Items);
        Assert.Equal(ServerSyncState.Stale, server.State);
        Assert.False(snapshot.IsAuthoritativeFor(_serverId));
    }

    [Fact]
    public async Task AMissingFileWithABackupIsRestoredToo()
    {
        await WriteGoodFileAsync(Server());
        await WriteGoodFileAsync(Server());
        File.Delete(MainPath);

        Assert.Equal(IndexHealth.RestoredFromBackup, NewFile().Load().Health);
    }

    [Fact]
    public async Task ADamagedBackupDoesNotHideADamagedFile()
    {
        await File.WriteAllTextAsync(MainPath, "damaged");
        await File.WriteAllTextAsync(BackupPath, "damaged too");

        Assert.Equal(IndexHealth.Corrupt, NewFile().Load().Health);
    }

    [Fact]
    public async Task ASaveLeavesNoTemporaryFile()
    {
        await WriteGoodFileAsync(Server());
        await WriteGoodFileAsync(Server());

        Assert.Equal(
            [IndexFile.FileName, IndexFile.FileName + ".bak"],
            System.Linq.Enumerable.Order(System.Linq.Enumerable.Select(Directory.GetFiles(_folder), path => Path.GetFileName(path)), StringComparer.Ordinal));
    }

    [Fact]
    public async Task ASaveThatFailsLeavesTheCurrentFileAlone()
    {
        await WriteGoodFileAsync(Server());
        var before = await File.ReadAllBytesAsync(MainPath);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewFile().SaveAsync(Snapshot(), keepCurrentAsBackup: true, cancellation.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(MainPath));
        Assert.False(File.Exists(MainPath + ".tmp"));
    }

    [Fact]
    public async Task ADamagedFileIsNotKeptAsTheBackup()
    {
        await WriteGoodFileAsync(Server());
        await WriteGoodFileAsync(Server());
        var goodBackup = await File.ReadAllBytesAsync(BackupPath);
        await File.WriteAllTextAsync(MainPath, "damaged");

        await NewFile().SaveAsync(Snapshot(), keepCurrentAsBackup: false, CancellationToken.None);

        Assert.Equal(goodBackup, await File.ReadAllBytesAsync(BackupPath));
    }

    [Fact]
    public async Task TheLogsDoNotQuoteTheFile()
    {
        await File.WriteAllTextAsync(MainPath, "{\"AccessToken\":\"" + TestValues.Token);

        NewFile().Load();

        Assert.NotEmpty(_logger.Lines);
        Assert.All(_logger.Lines, line => Assert.DoesNotContain(TestValues.Token, line, StringComparison.Ordinal));
    }
}
