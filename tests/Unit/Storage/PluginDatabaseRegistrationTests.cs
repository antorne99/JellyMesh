using Jellyfin.Plugin.JellyMesh.Storage;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Storage;

public class PluginDatabaseRegistrationTests
{
    // One instance for both names, shared by every part of the plugin that keeps its data in the database.
    [Fact]
    public void ThePluginRegistersOneDatabase()
    {
        var services = new ServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());

        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, service => service.ServiceType == typeof(PluginDatabase)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, service => service.ServiceType == typeof(IPluginDatabase)).Lifetime);
    }
}
