using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Index;

public sealed class ManualMergeStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jellymesh-merges-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_folder, ManualMergeStore.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private ManualMergeStore Store() => new(_folder, new CapturingLogger<ManualMergeStore>());

    private static ManualMerge Merge() => new()
    {
        Id = Guid.NewGuid(),
        CreatedUtc = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero),
        Copies =
        [
            new MergedCopy { ServerId = "a", ItemId = "1", Tmdb = "7", Title = "Film", Year = 2020 },
            new MergedCopy { ServerId = "local", ItemId = "2", IsLocal = true }
        ]
    };

    [Fact]
    public void AMissingFileMeansNoMerges()
    {
        using var store = Store();

        Assert.Empty(store.Merges);
        Assert.False(store.IsDamaged);
    }

    [Fact]
    public async Task MergesSurviveARestart()
    {
        var merge = Merge();
        using (var store = Store())
        {
            await store.UpdateAsync(_ => [merge], CancellationToken.None);
        }

        using var reloaded = Store();

        var read = Assert.Single(reloaded.Merges);
        Assert.Equal(merge.Id, read.Id);
        Assert.Equal(merge.CreatedUtc, read.CreatedUtc);
        Assert.Equal(merge.Copies, read.Copies);
    }

    [Fact]
    public async Task NoTemporaryFileStays()
    {
        using var store = Store();

        await store.UpdateAsync(_ => [Merge()], CancellationToken.None);

        Assert.Equal([ManualMergeStore.FileName], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Version\":99,\"Merges\":[]}")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":1,\"Merges\":[{\"Id\":\"00000000-0000-0000-0000-000000000001\"}]}")]
    public void ABadFileIsSetAsideAndReported(string content)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, content);

        using var store = Store();

        Assert.Empty(store.Merges);
        Assert.True(store.IsDamaged);
        Assert.Equal(content, File.ReadAllText(FilePath + ".damaged"));
    }

    [Fact]
    public async Task ASaveClearsTheWarning_AndTheDamagedCopyStays()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(FilePath, "not json");
        using var store = Store();

        await store.UpdateAsync(_ => [Merge()], CancellationToken.None);

        Assert.False(store.IsDamaged);
        Assert.True(File.Exists(FilePath + ".damaged"));
    }

    [Fact]
    public async Task AChangeThatFails_LeavesTheMergesAsTheyWere()
    {
        var merge = Merge();
        using var store = Store();
        await store.UpdateAsync(_ => [merge], CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(_ => throw new InvalidOperationException(), CancellationToken.None));

        Assert.Same(merge, Assert.Single(store.Merges));
    }

    [Fact]
    public async Task TheFileHoldsNoAddress()
    {
        using var store = Store();

        await store.UpdateAsync(_ => [Merge()], CancellationToken.None);

        var text = await File.ReadAllTextAsync(FilePath);
        Assert.DoesNotContain("http", text, StringComparison.OrdinalIgnoreCase);
    }
}
