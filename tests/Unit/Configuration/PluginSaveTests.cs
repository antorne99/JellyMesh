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
    public void TheXmlRoundTripsTheAutomaticLibraries()
    {
        _plugin.ChangeConfiguration(config =>
        {
            var server = config.Servers.Single();
            server.AutomaticLibraries = true;
            server.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Series, Name = "Friend - Serie", ItemId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0" });
            server.ReleasedAutomaticLibraries.Add("Friend - Film");
        });

        var loaded = Assert.Single(((PluginConfiguration)new RealXmlSerializer().DeserializeFromFile(typeof(PluginConfiguration), _plugin.ConfigurationFilePath)!).Servers);

        Assert.True(loaded.AutomaticLibraries);
        var record = Assert.Single(loaded.AutomaticLibraryRegistry);
        Assert.Equal((ServerLibraryKind.Series, "Friend - Serie", "0f1e2d3c4b5a69788796a5b4c3d2e1f0"), (record.Kind, record.Name, record.ItemId));
        Assert.Equal(["Friend - Film"], loaded.ReleasedAutomaticLibraries);
    }

    [Fact]
    public void TheXmlRoundTripsTheServerIdsAndTheRemovedServers()
    {
        var formerId = Guid.NewGuid();
        _plugin.ChangeConfiguration(config =>
        {
            config.Servers.Single().JellyfinServerId = "8e3f1c2a4b5d4e6f8a9b0c1d2e3f4a5b";
            config.FormerServers.Add(new FormerServerConfig { Id = formerId, JellyfinServerId = "1a2b3c4d5e6f40718293a4b5c6d7e8f9", Name = "Old" });
        });

        var loaded = (PluginConfiguration)new RealXmlSerializer().DeserializeFromFile(typeof(PluginConfiguration), _plugin.ConfigurationFilePath)!;

        Assert.Equal("8e3f1c2a4b5d4e6f8a9b0c1d2e3f4a5b", Assert.Single(loaded.Servers).JellyfinServerId);
        var former = Assert.Single(loaded.FormerServers);
        Assert.Equal((formerId, "1a2b3c4d5e6f40718293a4b5c6d7e8f9", "Old"), (former.Id, former.JellyfinServerId, former.Name));
    }

    [Fact]
    public void AStockSaveKeepsTheRemovedServersAndShowsThemNot()
    {
        _plugin.ChangeConfiguration(config => config.FormerServers.Add(new FormerServerConfig { Id = Guid.NewGuid(), JellyfinServerId = "1a2b3c4d5e6f40718293a4b5c6d7e8f9", Name = "Old" }));
        var json = JsonSerializer.Serialize(_plugin.Configuration, JsonDefaults.Options);
        var incoming = JsonSerializer.Deserialize<PluginConfiguration>(json, JsonDefaults.Options)!;

        _plugin.UpdateConfiguration(incoming);

        Assert.DoesNotContain("1a2b3c4d5e6f40718293a4b5c6d7e8f9", json, StringComparison.Ordinal);
        Assert.Equal("Old", Assert.Single(_plugin.Configuration.FormerServers).Name);
    }

    [Fact]
    public void TheLibrariesOfRemovedServersSurviveTheXmlAndAStockSaveAndAreNotShown()
    {
        var serverId = Guid.NewGuid();
        _plugin.ChangeConfiguration(config =>
        {
            config.HoldAutomaticLibraries(
                serverId,
                [new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Casa - Film", ItemId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0" }],
                ["9a8b7c6d5e4f30211203f4e5d6c7b8a9"],
                ["5d6c7b8a99a8b7c6d5e4f30211203f4e"],
                true);
            config.RemovedServerLibraries[0].ReleasedLibraries.Add("Casa - Serie");
            config.RemovedServerLibraries[0].HiddenReleasedLibraries.Add("Casa - Serie");
        });
        var json = JsonSerializer.Serialize(_plugin.Configuration, JsonDefaults.Options);
        _plugin.UpdateConfiguration(JsonSerializer.Deserialize<PluginConfiguration>(json, JsonDefaults.Options)!);

        var loaded = (PluginConfiguration)new RealXmlSerializer().DeserializeFromFile(typeof(PluginConfiguration), _plugin.ConfigurationFilePath)!;

        Assert.DoesNotContain("0f1e2d3c4b5a69788796a5b4c3d2e1f0", json, StringComparison.Ordinal);
        var held = Assert.Single(loaded.RemovedServerLibraries);
        Assert.Equal(serverId, held.ServerId);
        Assert.Equal("Casa - Film", Assert.Single(held.Libraries).Name);
        Assert.Equal(["9a8b7c6d5e4f30211203f4e5d6c7b8a9"], held.PreexistingLibraryIds);
        Assert.Equal(["5d6c7b8a99a8b7c6d5e4f30211203f4e"], held.FollowedLibraryIds);
        Assert.True(held.FollowSkipped);
        Assert.Equal(["Casa - Serie"], held.ReleasedLibraries);
        Assert.Equal(["Casa - Serie"], held.HiddenReleasedLibraries);
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

    [Fact]
    public void TheVariableSetsTheFolderAndSavesIt()
    {
        var logger = new CapturingLogger<Plugin>();

        var loaded = LoadWith(" /federata ", logger);

        Assert.Equal("/federata", loaded.Configuration.FederatedFolderPath);
        Assert.Contains("<FederatedFolderPath>/federata</FederatedFolderPath>", File.ReadAllText(loaded.ConfigurationFilePath), StringComparison.Ordinal);
        Assert.Empty(logger.Lines);
    }

    [Fact]
    public void TheVariableWinsOverASavedFolder()
    {
        _plugin.UpdateConfiguration(new PluginConfiguration { FederatedFolderPath = "/chosen" });

        var loaded = LoadWith("/federata", new CapturingLogger<Plugin>());

        Assert.Equal("/federata", loaded.Configuration.FederatedFolderPath);
    }

    [Fact]
    public void AStockSaveCannotChangeTheFolderOfTheVariable()
    {
        var loaded = LoadWith("/federata", new CapturingLogger<Plugin>());

        loaded.UpdateConfiguration(new PluginConfiguration { FederatedFolderPath = "/chosen", SyncIntervalSeconds = 600 });

        Assert.Equal("/federata", loaded.Configuration.FederatedFolderPath);
        Assert.Equal(600, loaded.Configuration.SyncIntervalSeconds);
    }

    [Fact]
    public void AStockSaveCannotClearTheFolderOfTheVariable()
    {
        var loaded = LoadWith("/federata", new CapturingLogger<Plugin>());

        loaded.UpdateConfiguration(new PluginConfiguration());

        Assert.Equal("/federata", loaded.Configuration.FederatedFolderPath);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/")]
    [InlineData("/films/../other")]
    public void AnUnusableVariableIsIgnoredWithAWarning(string value)
    {
        _plugin.UpdateConfiguration(new PluginConfiguration { FederatedFolderPath = "/chosen" });
        var logger = new CapturingLogger<Plugin>();

        var loaded = LoadWith(value, logger);
        loaded.UpdateConfiguration(new PluginConfiguration { FederatedFolderPath = "/typed" });

        Assert.Equal("/typed", loaded.Configuration.FederatedFolderPath);
        var line = Assert.Single(logger.Lines);
        Assert.Contains(EnvironmentFederatedRootSource.VariableName, line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutTheVariableTheFolderStaysTheChoiceOfTheUser(string? value)
    {
        _plugin.UpdateConfiguration(new PluginConfiguration { FederatedFolderPath = "/chosen" });
        var logger = new CapturingLogger<Plugin>();

        var loaded = LoadWith(value, logger);

        Assert.Equal("/chosen", loaded.Configuration.FederatedFolderPath);
        Assert.Empty(logger.Lines);
    }

    public void Dispose() => Directory.Delete(_folder, true);

    private Plugin LoadWith(string? federatedRoot, CapturingLogger<Plugin> logger)
        => new(_paths, new RealXmlSerializer(), logger, new FixedFederatedRootSource(federatedRoot));

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
