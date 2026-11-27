using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Every global setting in one object, as <c>GET /JellyMesh/Admin/Settings</c> gives it. Holds no servers, peers or
/// credentials.
/// </summary>
public sealed class PluginSettings
{
    /// <summary>Gets <see cref="PluginConfiguration.SyncIntervalSeconds"/>.</summary>
    public int SyncIntervalSeconds { get; init; }

    /// <summary>Gets <see cref="PluginConfiguration.ReconciliationTime"/>.</summary>
    public string ReconciliationTime { get; init; } = string.Empty;

    /// <summary>Gets <see cref="PluginConfiguration.FederatedFolderPath"/>.</summary>
    public string FederatedFolderPath { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether <see cref="PluginConfiguration.MetadataProtection"/> is on.</summary>
    public bool MetadataProtection { get; init; }

    /// <summary>Gets a value indicating whether <see cref="PluginConfiguration.FederateCollections"/> is on.</summary>
    public bool FederateCollections { get; init; }

    /// <summary>Gets a value indicating whether <see cref="PluginConfiguration.CloseTranscodeAtSlotExpiry"/> is on.</summary>
    public bool CloseTranscodeAtSlotExpiry { get; init; }

    /// <summary>Gets <see cref="PluginConfiguration.RemotePlaybackInfoTimeoutSeconds"/>.</summary>
    public int RemotePlaybackInfoTimeoutSeconds { get; init; }

    /// <summary>Gets <see cref="PluginConfiguration.StreamOpenTimeoutSeconds"/>.</summary>
    public int StreamOpenTimeoutSeconds { get; init; }

    /// <summary>Gets <see cref="PluginConfiguration.PlaybackIdleSeconds"/>.</summary>
    public int PlaybackIdleSeconds { get; init; }

    /// <summary>Gets <see cref="PluginConfiguration.SessionRetentionHours"/>.</summary>
    public int SessionRetentionHours { get; init; }

    /// <summary>Gets <see cref="PluginConfiguration.PolicyCacheMinutes"/>.</summary>
    public int PolicyCacheMinutes { get; init; }

    /// <summary>Gets <see cref="PluginConfiguration.NewServerLibraryMode"/>.</summary>
    public LibraryMode NewServerLibraryMode { get; init; }

    /// <summary>Gets the settings an environment variable decides, which the page shows read-only.</summary>
    public IReadOnlyList<FixedSetting> FixedByEnvironment { get; init; } = [];

    /// <summary>
    /// Reads the settings of a configuration.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="fixedByEnvironment">The settings an environment variable decides.</param>
    /// <returns>The settings.</returns>
    public static PluginSettings From(PluginConfiguration config, IReadOnlyList<FixedSetting> fixedByEnvironment)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new PluginSettings
        {
            SyncIntervalSeconds = config.SyncIntervalSeconds,
            ReconciliationTime = config.ReconciliationTime,
            FederatedFolderPath = config.FederatedFolderPath,
            MetadataProtection = config.MetadataProtection,
            FederateCollections = config.FederateCollections,
            CloseTranscodeAtSlotExpiry = config.CloseTranscodeAtSlotExpiry,
            RemotePlaybackInfoTimeoutSeconds = config.RemotePlaybackInfoTimeoutSeconds,
            StreamOpenTimeoutSeconds = config.StreamOpenTimeoutSeconds,
            PlaybackIdleSeconds = config.PlaybackIdleSeconds,
            SessionRetentionHours = config.SessionRetentionHours,
            PolicyCacheMinutes = config.PolicyCacheMinutes,
            NewServerLibraryMode = config.NewServerLibraryMode,
            FixedByEnvironment = fixedByEnvironment
        };
    }
}
