using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

/// <summary>What the stock endpoints do: GET serializes the config, POST replaces it from JSON.</summary>
public sealed class PluginSaveTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("jellymesh-tests").FullName;
    private readonly IApplicationPaths _paths;
    private readonly Plugin _plugin;

    public PluginSaveTests()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_folder);
        paths.SetupGet(p => p.PluginsPath).Returns(_folder);
        _paths = paths.Object;
        _plugin = new Plugin(_paths, new RealXmlSerializer(), new CapturingLogger<Plugin>());
        var server = new RemoteServerConfig
        {
            Id = Guid.NewGuid(),
            Name = "Friend",
            Url = TestValues.Url,
            EncryptedCredentials = "ciphertext-sentinel"
        };
        server.ImportedLibraryIds.Add("library-1");
        _plugin.ChangeConfiguration(config => config.Servers.Add(server));
    }

    private string Xml => File.ReadAllText(_plugin.ConfigurationFilePath);

    [Fact]
    public void TheStockGetCarriesNeitherCredentialsNorAddresses()
    {
        var json = JsonSerializer.Serialize<object>(_plugin.Configuration, JsonDefaults.Options);

        Assert.DoesNotContain("ciphertext-sentinel", json, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Host, json, StringComparison.Ordinal);
        Assert.Contains("SyncIntervalSeconds", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AStockSaveTellsTheStoreOnce()
    {
        var store = new PluginConfigurationStore();
        var seen = new System.Collections.Generic.List<int>();
        store.SettingsChanged += (_, _) => seen.Add(store.Read(config => config.SyncIntervalSeconds));

        _plugin.UpdateConfiguration(new PluginConfiguration { SyncIntervalSeconds = 600 });

        Assert.Equal([600], seen);
    }

    [Fact]
    public void AChangeOfTheServersDoesNotTouchTheSchedule()
    {
        var store = new PluginConfigurationStore();
        var changes = 0;
        store.SettingsChanged += (_, _) => changes++;

        store.Update(config => config.Servers.Clear());

        Assert.Equal(0, changes);
    }

    [Fact]
    public void TheCredentialsStillReachTheXml()
    {
        Assert.Contains("ciphertext-sentinel", Xml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheXmlRoundTripsTheServers()
    {
        var loaded = (PluginConfiguration)new RealXmlSerializer().DeserializeFromFile(typeof(PluginConfiguration), _plugin.ConfigurationFilePath)!;

        var server = Assert.Single(loaded.Servers);
        Assert.Equal("ciphertext-sentinel", server.EncryptedCredentials);
        Assert.Equal(["library-1"], server.ImportedLibraryIds);
    }

    [Fact]
    public void AStockSaveKeepsTheServers()
    {
        var incoming = JsonSerializer.Deserialize<PluginConfiguration>(
            "{\"SyncIntervalSeconds\":600,\"ReconciliationTime\":\"04:30\",\"Servers\":[{\"Name\":\"Injected\"}]}",
            JsonDefaults.Options)!;

        _plugin.UpdateConfiguration(incoming);

        Assert.Equal(600, _plugin.Configuration.SyncIntervalSeconds);
        Assert.Equal("Friend", Assert.Single(_plugin.Configuration.Servers).Name);
        Assert.Contains("ciphertext-sentinel", Xml, StringComparison.Ordinal);
        Assert.DoesNotContain("Injected", Xml, StringComparison.Ordinal);
    }

    [Fact]
    public void ARoundTripThroughTheBrowserKeepsTheServers()
    {
        var json = JsonSerializer.Serialize(_plugin.Configuration, JsonDefaults.Options);
        var incoming = JsonSerializer.Deserialize<PluginConfiguration>(json, JsonDefaults.Options)!;

        _plugin.UpdateConfiguration(incoming);

        Assert.Single(_plugin.Configuration.Servers);
    }

    [Fact]
    public void AnInvalidSaveChangesNothing()
    {
        var incoming = new PluginConfiguration { SyncIntervalSeconds = 0 };

        Assert.Throws<ArgumentException>(() => _plugin.UpdateConfiguration(incoming));

        Assert.Equal(900, _plugin.Configuration.SyncIntervalSeconds);
        Assert.Single(_plugin.Configuration.Servers);
    }

    [Fact]
    public void ASaveBelowTheMinimumIntervalIsRefused()
    {
        Assert.Throws<ArgumentException>(() => _plugin.UpdateConfiguration(new PluginConfiguration { SyncIntervalSeconds = 299 }));

        Assert.Equal(900, _plugin.Configuration.SyncIntervalSeconds);
    }

    [Fact]
    public void AnIntervalAlreadySavedBelowTheMinimumRunsAsTheMinimumWithAWarning()
    {
        var path = _plugin.ConfigurationFilePath;
        _plugin.ChangeConfiguration(config => config.SyncIntervalSeconds = 60);
        Assert.Contains("<SyncIntervalSeconds>60<", File.ReadAllText(path), StringComparison.Ordinal);
        var logger = new CapturingLogger<Plugin>();

        var loaded = new Plugin(_paths, new RealXmlSerializer(), logger);

        Assert.Equal(300, loaded.Configuration.SyncIntervalSeconds);
        Assert.Contains("\"SyncIntervalSeconds\":300", JsonSerializer.Serialize<object>(loaded.Configuration, JsonDefaults.Options), StringComparison.OrdinalIgnoreCase);
        var line = Assert.Single(logger.Lines);
        Assert.Contains("SyncIntervalSeconds", line, StringComparison.Ordinal);
        Assert.Contains("60", line, StringComparison.Ordinal);
        Assert.Contains("300", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AConfigurationSavedBeforeThePlaybackTimesGetsTheDefaults()
    {
        var path = _plugin.ConfigurationFilePath;
        _plugin.ChangeConfiguration(config => config.PlaybackIdleSeconds = 45);
        File.WriteAllText(path, Regex.Replace(File.ReadAllText(path), "\\s*<PlaybackIdleSeconds>.*</PlaybackIdleSeconds>", string.Empty));
        var logger = new CapturingLogger<Plugin>();

        var loaded = new Plugin(_paths, new RealXmlSerializer(), logger);

        Assert.Equal(90, loaded.Configuration.PlaybackIdleSeconds);
        Assert.Empty(logger.Lines);
    }

    [Fact]
    public void AnIdleTimeSavedAboveTheLimitRunsAsTheLimitWithAWarning()
    {
        _plugin.ChangeConfiguration(config => config.PlaybackIdleSeconds = 200);
        var logger = new CapturingLogger<Plugin>();

        var loaded = new Plugin(_paths, new RealXmlSerializer(), logger);

        Assert.Equal(109, loaded.Configuration.PlaybackIdleSeconds);
        Assert.Contains("PlaybackIdleSeconds", Assert.Single(logger.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void ASaveWithAnIdleTimeAboveTheLimitIsRefused()
    {
        Assert.Throws<ArgumentException>(() => _plugin.UpdateConfiguration(new PluginConfiguration { PlaybackIdleSeconds = 110 }));

        Assert.Equal(90, _plugin.Configuration.PlaybackIdleSeconds);
    }

    [Fact]
    public void AValidConfigurationLoadsWithoutAWarning()
    {
        var logger = new CapturingLogger<Plugin>();

        _ = new Plugin(_paths, new RealXmlSerializer(), logger);

        Assert.Empty(logger.Lines);
    }

    public void Dispose() => Directory.Delete(_folder, true);

    private sealed class RealXmlSerializer : IXmlSerializer
    {
        public object? DeserializeFromStream(Type type, Stream stream) => new XmlSerializer(type).Deserialize(stream);

        public void SerializeToStream(object obj, Stream stream) => new XmlSerializer(obj.GetType()).Serialize(stream, obj);

        public void SerializeToFile(object obj, string file)
        {
            using var stream = File.Create(file);
            SerializeToStream(obj, stream);
        }

        public object? DeserializeFromFile(Type type, string file)
        {
            using var stream = File.OpenRead(file);
            return DeserializeFromStream(type, stream);
        }

        public object? DeserializeFromBytes(Type type, byte[] buffer)
        {
            using var stream = new MemoryStream(buffer);
            return DeserializeFromStream(type, stream);
        }
    }
}
