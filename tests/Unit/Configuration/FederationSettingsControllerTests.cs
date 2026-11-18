using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

/// <summary>GET and PUT of /JellyMesh/Admin/Settings, which replace the stock configuration endpoint (§3.2).</summary>
public sealed class FederationSettingsControllerTests
{
    private readonly InMemoryConfigurationStore _store = new();

    [Fact]
    public void EveryEndpointRequiresAnAdministrator()
    {
        var type = typeof(FederationSettingsController);

        Assert.Equal(Policies.RequiresElevation, type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal("JellyMesh/Admin", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.DoesNotContain(
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.GetCustomAttribute<AllowAnonymousAttribute>() is not null);
        Assert.Equal("Settings", type.GetMethod(nameof(FederationSettingsController.GetSettings))!.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("Settings", type.GetMethod(nameof(FederationSettingsController.SetSettings))!.GetCustomAttribute<HttpPutAttribute>()!.Template);
    }

    [Fact]
    public void TheSettingsComeInOneObjectWithoutServersOrCredentials()
    {
        _store.Configuration.SyncIntervalSeconds = 1200;
        _store.Configuration.FederatedFolderPath = "/federata";
        _store.Configuration.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa", Url = TestValues.Url, EncryptedCredentials = "ciphertext-sentinel" });
        _store.Configuration.Peers.Add(new ProviderPeerConfig { UserId = Guid.NewGuid(), MaxStreams = 2 });

        var settings = Controller().GetSettings().Value!;
        var json = JsonSerializer.Serialize(settings, JsonDefaults.Options);

        Assert.Equal((1200, "/federata", "03:00", true), (settings.SyncIntervalSeconds, settings.FederatedFolderPath, settings.ReconciliationTime, settings.MetadataProtection));
        Assert.Empty(settings.FixedByEnvironment);
        Assert.DoesNotContain("ciphertext-sentinel", json, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Host, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Casa", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Peers", json, StringComparison.Ordinal);
    }

    [Fact]
    public void APutSavesEverySettingAndAnswersWithThem()
    {
        var result = Controller().SetSettings(new SettingsRequest
        {
            SyncIntervalSeconds = 600,
            ReconciliationTime = "04:30",
            FederatedFolderPath = " /federata ",
            MetadataProtection = false,
            FederateCollections = true,
            CloseTranscodeAtSlotExpiry = false,
            RemotePlaybackInfoTimeoutSeconds = 7,
            StreamOpenTimeoutSeconds = 11,
            PlaybackIdleSeconds = 45,
            SessionRetentionHours = 3,
            PolicyCacheMinutes = 9
        });

        var config = _store.Configuration;
        Assert.Equal(
            (600, "04:30", "/federata", false, true, false, 7, 11, 45, 3, 9),
            (config.SyncIntervalSeconds, config.ReconciliationTime, config.FederatedFolderPath, config.MetadataProtection, config.FederateCollections, config.CloseTranscodeAtSlotExpiry,
                config.RemotePlaybackInfoTimeoutSeconds, config.StreamOpenTimeoutSeconds, config.PlaybackIdleSeconds, config.SessionRetentionHours, config.PolicyCacheMinutes));
        Assert.Equal(600, result.Value!.SyncIntervalSeconds);
    }

    [Fact]
    public void WhatTheGetGaveCanBePutBack()
    {
        _store.Configuration.SyncIntervalSeconds = 1200;
        var json = JsonSerializer.Serialize(Controller().GetSettings().Value, JsonDefaults.Options);

        var request = JsonSerializer.Deserialize<SettingsRequest>(json, JsonDefaults.Options)!;
        Controller().SetSettings(request);

        Assert.Equal(1200, _store.Configuration.SyncIntervalSeconds);
        Assert.Equal(1, _store.Saves);
    }

    [Fact]
    public void ASettingLeftOutKeepsItsValue()
    {
        _store.Configuration.PlaybackIdleSeconds = 45;

        Controller().SetSettings(new SettingsRequest { SyncIntervalSeconds = 600 });

        Assert.Equal((600, 45), (_store.Configuration.SyncIntervalSeconds, _store.Configuration.PlaybackIdleSeconds));
    }

    [Theory]
    [InlineData(nameof(SettingsRequest.SyncIntervalSeconds), 299)]
    [InlineData(nameof(SettingsRequest.RemotePlaybackInfoTimeoutSeconds), 61)]
    [InlineData(nameof(SettingsRequest.StreamOpenTimeoutSeconds), 4)]
    [InlineData(nameof(SettingsRequest.PlaybackIdleSeconds), 110)]
    [InlineData(nameof(SettingsRequest.SessionRetentionHours), 0)]
    [InlineData(nameof(SettingsRequest.PolicyCacheMinutes), 61)]
    public void AValueOutOfRangeIsRefusedWithItsFieldAndNothingIsSaved(string field, int value)
    {
        var request = new SettingsRequest { MetadataProtection = false };
        typeof(SettingsRequest).GetProperty(field)!.SetValue(request, value);

        var refusal = Refusal(Controller().SetSettings(request));

        Assert.Equal(field, refusal.Field);
        Assert.Contains(field, refusal.Message, StringComparison.Ordinal);
        Assert.True(_store.Configuration.MetadataProtection);
        Assert.Equal(900, _store.Configuration.SyncIntervalSeconds);
    }

    [Theory]
    [InlineData(nameof(SettingsRequest.ReconciliationTime), "25:00")]
    [InlineData(nameof(SettingsRequest.FederatedFolderPath), "relative/path")]
    [InlineData(nameof(SettingsRequest.FederatedFolderPath), "/")]
    public void AnInvalidTextIsRefusedWithItsField(string field, string value)
    {
        var request = new SettingsRequest();
        typeof(SettingsRequest).GetProperty(field)!.SetValue(request, value);

        Assert.Equal(field, Refusal(Controller().SetSettings(request)).Field);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public void ASettingOfTheEnvironmentIsReadOnlyWithItsVariable()
    {
        _store.Configuration.FederatedFolderPath = "/federata";

        var settings = Controller("/federata").GetSettings().Value!;

        var fixedSetting = Assert.Single(settings.FixedByEnvironment);
        Assert.Equal((nameof(SettingsRequest.FederatedFolderPath), EnvironmentFederatedRootSource.VariableName), (fixedSetting.Field, fixedSetting.Variable));
    }

    [Fact]
    public void AnUnusableVariableFixesNothing()
    {
        Assert.Empty(Controller("relative/path").GetSettings().Value!.FixedByEnvironment);
    }

    // D20: a value other than the one of the variable is refused like one out of range.
    [Fact]
    public void AValueOtherThanTheOneOfTheVariableIsRefusedWithItsField()
    {
        _store.Configuration.FederatedFolderPath = "/federata";

        var refusal = Refusal(Controller("/federata").SetSettings(new SettingsRequest { FederatedFolderPath = "/chosen", SyncIntervalSeconds = 600 }));

        Assert.Equal(nameof(SettingsRequest.FederatedFolderPath), refusal.Field);
        Assert.Contains(EnvironmentFederatedRootSource.VariableName, refusal.Message, StringComparison.Ordinal);
        Assert.Equal(("/federata", 900), (_store.Configuration.FederatedFolderPath, _store.Configuration.SyncIntervalSeconds));
    }

    [Fact]
    public void AnEmptyFolderUnderTheVariableIsRefusedToo()
    {
        _store.Configuration.FederatedFolderPath = "/federata";

        Assert.Equal(nameof(SettingsRequest.FederatedFolderPath), Refusal(Controller("/federata").SetSettings(new SettingsRequest { FederatedFolderPath = string.Empty })).Field);
    }

    [Fact]
    public void TheValueOfTheVariableIsAccepted()
    {
        _store.Configuration.FederatedFolderPath = "/federata";

        Controller("/federata").SetSettings(new SettingsRequest { FederatedFolderPath = "/federata", SyncIntervalSeconds = 600 });

        Assert.Equal(("/federata", 600), (_store.Configuration.FederatedFolderPath, _store.Configuration.SyncIntervalSeconds));
    }

    [Fact]
    public void ASuspendedDatabaseAnswersUnavailable()
    {
        _store.SaveFailure = new StoreSuspendedException("test");

        var result = Assert.IsType<ObjectResult>(Controller().SetSettings(new SettingsRequest { SyncIntervalSeconds = 600 }).Result);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal(FederationSettingsController.StoreSuspended, Assert.IsType<FailureResponse>(result.Value).Message);
    }

    [Fact]
    public void AFailedSaveLeavesEverySettingAsItWas()
    {
        _store.Configuration.PlaybackIdleSeconds = 45;
        _store.SaveFailure = new InvalidOperationException("disk full");

        Assert.Throws<InvalidOperationException>(() => Controller().SetSettings(new SettingsRequest { SyncIntervalSeconds = 600, PlaybackIdleSeconds = 60, FederatedFolderPath = "/federata" }));

        Assert.Equal((900, 45, string.Empty), (_store.Configuration.SyncIntervalSeconds, _store.Configuration.PlaybackIdleSeconds, _store.Configuration.FederatedFolderPath));
    }

    private static SettingRefusal Refusal(ActionResult<PluginSettings> result)
        => Assert.IsType<SettingRefusal>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);

    private FederationSettingsController Controller(string? root = null) => new(_store, new FixedFederatedRootSource(root));
}
