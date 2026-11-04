using System;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

public class PluginConfigurationTests
{
    [Fact]
    public void DefaultsFollowThePlan()
    {
        var config = new PluginConfiguration();

        Assert.Equal(900, config.SyncIntervalSeconds);
        Assert.Equal("03:00", config.ReconciliationTime);
        Assert.True(config.MetadataProtection);
        Assert.Empty(config.FederatedFolderPath);
        Assert.Empty(config.Servers);
        config.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void IntervalMustBePositive(int seconds)
    {
        Assert.Throws<ArgumentException>(new PluginConfiguration { SyncIntervalSeconds = seconds }.Validate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("3:00")]
    [InlineData("24:00")]
    [InlineData("03:60")]
    [InlineData("03:00:00")]
    [InlineData("3am")]
    public void TimeMustBeHoursAndMinutes(string time)
    {
        Assert.Throws<ArgumentException>(new PluginConfiguration { ReconciliationTime = time }.Validate);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("/federata/../etc")]
    public void FolderMustBeAUsableAbsolutePath(string path)
    {
        Assert.Throws<ArgumentException>(new PluginConfiguration { FederatedFolderPath = path }.Validate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/federata")]
    [InlineData("/mnt/data/federata/")]
    public void FolderCanBeEmptyOrAbsolute(string path)
    {
        new PluginConfiguration { FederatedFolderPath = path }.Validate();
    }

    [Fact]
    public void AdoptingFromItselfKeepsTheServers()
    {
        var config = new PluginConfiguration();
        config.Servers.Add(new RemoteServerConfig());

        config.AdoptServersFrom(config);

        Assert.Single(config.Servers);
    }
}
