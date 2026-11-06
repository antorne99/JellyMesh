using System;
using System.IO;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

/// <summary>What the plugin tells the Dashboard about its page.</summary>
public sealed class PluginPagesTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("jellymesh-tests").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void ThePluginPageIsInTheDashboardMenuAndCanBeServed()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_folder);
        paths.SetupGet(p => p.PluginsPath).Returns(_folder);
        var serializer = new Mock<IXmlSerializer>();
        serializer.Setup(s => s.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(new PluginConfiguration());
        var plugin = new Plugin(paths.Object, serializer.Object, new CapturingLogger<Plugin>());

        var page = Assert.Single(plugin.GetPages());

        Assert.True(page.EnableInMainMenu);
        Assert.Equal("hub", page.MenuIcon);
        using var resource = typeof(Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
        Assert.NotNull(resource);
    }
}
