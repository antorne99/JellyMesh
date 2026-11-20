using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The stub providers on an audiobook part: 12.4 does not store IsShortcut on an AudioBook, so it is set on every refresh (F48).
public sealed class AudioBookProviderTests : IDisposable
{
    private readonly StubFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private StubBeforeProbeProvider Before() => new(_fixture.Registry, _fixture.Sync.Store, _fixture.Technical);

    private StubAfterProbeProvider After() => new(_fixture.Registry, _fixture.Sync.Store, _fixture.Technical);

    private static MetadataRefreshOptions Options() => new(new Mock<IDirectoryService>().Object) { EnableRemoteContentProbe = true };

    private static string WriteStrm(string path, string url)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, url + "\n");
        return path;
    }

    [Fact]
    public void TheyRunOnAudiobooks()
    {
        Assert.IsAssignableFrom<ICustomMetadataProvider<AudioBook>>(Before());
        Assert.IsAssignableFrom<ICustomMetadataProvider<AudioBook>>(After());
    }

    [Fact]
    public async Task AnAudiobookStrmInTheFolderIsAShortcutToItsUrlAndIsNotProbed()
    {
        var path = WriteStrm(_fixture.Path_("Libri", "Book", "01 - Part.strm"), "http://127.0.0.1:8096/JellyMesh/Strm/abc?sig=x");
        var item = new AudioBook { Id = Guid.NewGuid(), Path = path, IsLocked = true };
        var options = Options();

        await Before().FetchAsync(item, options, CancellationToken.None);

        Assert.False(options.EnableRemoteContentProbe);
        Assert.True(item.IsShortcut);
        Assert.Equal("http://127.0.0.1:8096/JellyMesh/Strm/abc?sig=x", item.ShortcutPath);
    }

    [Fact]
    public async Task AnAudiobookOutsideTheFolderIsLeftAlone()
    {
        var path = WriteStrm(Path.Combine(_fixture.Root + "-elsewhere", "Book.strm"), "http://example.invalid/book");
        var item = new AudioBook { Id = Guid.NewGuid(), Path = path };
        var options = Options();

        await Before().FetchAsync(item, options, CancellationToken.None);

        Assert.True(options.EnableRemoteContentProbe);
        Assert.False(item.IsShortcut);
        Assert.Null(item.ShortcutPath);
    }
}
