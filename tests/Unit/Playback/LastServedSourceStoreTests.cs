using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Playback;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

public sealed class LastServedSourceStoreTests : IDisposable
{
    private static readonly Guid _user = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _otherUser = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid _server = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _otherServer = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jellymesh-last-served-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_folder, LastServedSourceStore.FileName);

    public void Dispose()
    {
        if (File.Exists(_folder))
        {
            File.Delete(_folder);
        }
        else if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private LastServedSourceStore Store(CapturingLogger<LastServedSourceStore>? logger = null) => new(_folder, logger ?? new CapturingLogger<LastServedSourceStore>());

    [Fact]
    public void NothingIsKnown_WithoutAFile()
    {
        using var store = Store();

        Assert.Null(store.Find(_user, "stub"));
    }

    [Fact]
    public async Task TheServerSurvivesARestart()
    {
        using (var store = Store())
        {
            await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        }

        using var reloaded = Store();

        Assert.Equal(_server, reloaded.Find(_user, "stub"));
    }

    [Fact]
    public async Task EachUserKeepsTheirOwnServer_PerStub()
    {
        using (var store = Store())
        {
            await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
            await store.RecordAsync(_otherUser, "stub", _otherServer, CancellationToken.None);
            await store.RecordAsync(_user, "other-stub", _otherServer, CancellationToken.None);
        }

        using var reloaded = Store();

        Assert.Equal(_server, reloaded.Find(_user, "stub"));
        Assert.Equal(_otherServer, reloaded.Find(_otherUser, "stub"));
        Assert.Equal(_otherServer, reloaded.Find(_user, "other-stub"));
        Assert.Null(reloaded.Find(_otherUser, "other-stub"));
    }

    [Fact]
    public async Task TheLatestServerReplacesTheOneBefore()
    {
        using (var store = Store())
        {
            await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
            await store.RecordAsync(_user, "stub", _otherServer, CancellationToken.None);
        }

        using var reloaded = Store();

        Assert.Equal(_otherServer, reloaded.Find(_user, "stub"));
    }

    [Fact]
    public async Task TheSameServerAgain_DoesNotRewriteTheFile()
    {
        using var store = Store();
        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        var written = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(FilePath, written);

        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);

        Assert.Equal(written, File.GetLastWriteTimeUtc(FilePath));
    }

    [Fact]
    public async Task NoTemporaryFileStays()
    {
        using var store = Store();

        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);

        Assert.Equal([LastServedSourceStore.FileName], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Version\":99,\"Entries\":[]}")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":1,\"Entries\":[{\"UserId\":\"11111111-1111-1111-1111-111111111111\"}]}")]
    public void ABadFileIsSetAside(string content)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, content);

        using var store = Store();

        Assert.Null(store.Find(_user, "stub"));
        Assert.Equal(content, File.ReadAllText(FilePath + ".damaged"));
    }

    [Fact]
    public async Task AFailedSave_IsLoggedAndKeptInMemory()
    {
        // A file where the folder should be: nothing can be written under it.
        await File.WriteAllTextAsync(_folder, "in the way");
        var logger = new CapturingLogger<LastServedSourceStore>();
        using var store = Store(logger);

        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);

        Assert.Equal(_server, store.Find(_user, "stub"));
        Assert.NotEmpty(logger.Lines);
    }

    [Fact]
    public async Task TheFileNamesTheServerById_AndHoldsNothingElseOfIt()
    {
        using (var store = Store())
        {
            await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        }

        var text = await File.ReadAllTextAsync(FilePath);

        Assert.Contains(_server.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", text, StringComparison.OrdinalIgnoreCase);
    }
}
