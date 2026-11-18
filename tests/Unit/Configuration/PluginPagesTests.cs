using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

/// <summary>What the plugin tells Jellyfin about itself and its page.</summary>
public sealed class PluginPagesTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("jellymesh-tests").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void ThePluginPageIsInTheDashboardMenuAndCanBeServed()
    {
        var page = Assert.Single(NewPlugin().GetPages());

        Assert.True(page.EnableInMainMenu);
        Assert.Equal("hub", page.MenuIcon);
        using var resource = typeof(Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
        Assert.NotNull(resource);
    }

    // Jellyfin reads these right after construction; a plugin without them does not load (F23).
    [Fact]
    public void ThePluginSetsWhatJellyfinReadsWhenItLoadsIt()
    {
        var plugin = NewPlugin();

        Assert.Equal(typeof(Plugin).Assembly.GetName().Version, plugin.Version);
        Assert.Equal(typeof(Plugin).Assembly.Location, plugin.AssemblyFilePath);
        Assert.Equal(Path.Combine(_folder, "Jellyfin.Plugin.JellyMesh"), plugin.DataFolderPath);
    }

    // A data folder named as the plugin would be taken for an install of it and the real one deleted (F23).
    [Fact]
    public void TheDataFolderIsNotNamedAsThePlugin()
    {
        var plugin = NewPlugin();

        Assert.NotEqual(plugin.Name, Path.GetFileName(plugin.DataFolderPath), StringComparer.OrdinalIgnoreCase);
    }

    // No Jellyfin configuration, so Jellyfin writes no XML and its configuration endpoint answers 404 (§3.2).
    [Fact]
    public void ThePluginHasNoJellyfinConfiguration()
    {
        Assert.False(typeof(IHasPluginConfiguration).IsAssignableFrom(typeof(Plugin)));
    }

    private Plugin NewPlugin()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(_folder);
        return new Plugin(paths.Object);
    }
}
