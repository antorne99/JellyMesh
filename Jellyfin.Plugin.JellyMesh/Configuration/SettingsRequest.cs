using System;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The settings to save, in the shape <see cref="PluginSettings"/> has; a setting left out keeps its value.
/// </summary>
public sealed class SettingsRequest
{
    /// <summary>Gets or sets <see cref="PluginConfiguration.SyncIntervalSeconds"/>.</summary>
    public int? SyncIntervalSeconds { get; set; }

    /// <summary>Gets or sets <see cref="PluginConfiguration.ReconciliationTime"/>.</summary>
    public string? ReconciliationTime { get; set; }

    /// <summary>Gets or sets <see cref="PluginConfiguration.FederatedFolderPath"/>, trimmed before use.</summary>
    public string? FederatedFolderPath { get; set; }

    /// <summary>Gets or sets <see cref="PluginConfiguration.MetadataProtection"/>.</summary>
    public bool? MetadataProtection { get; set; }

    /// <summary>Gets or sets <see cref="PluginConfiguration.FederateCollections"/>.</summary>
    public bool? FederateCollections { get; set; }

    /// <summary>Gets or sets <see cref="PluginConfiguration.CloseTranscodeAtSlotExpiry"/>.</summary>
    public bool? CloseTranscodeAtSlotExpiry { get; set; }

    /// <summary>Gets or sets <see cref="PluginConfiguration.RemotePlaybackInfoTimeoutSeconds"/>.</summary>
    public int? RemotePlaybackInfoTimeoutSeconds { get; set; }

    /// <summary>Gets or sets <see cref="PluginConfiguration.StreamOpenTimeoutSeconds"/>.</summary>
    public int? StreamOpenTimeoutSeconds { get; set; }

    /// <summary>Gets or sets <see cref="PluginConfiguration.PlaybackIdleSeconds"/>.</summary>
    public int? PlaybackIdleSeconds { get; set; }

    /// <summary>Gets or sets <see cref="PluginConfiguration.SessionRetentionHours"/>.</summary>
    public int? SessionRetentionHours { get; set; }

    /// <summary>Gets or sets <see cref="PluginConfiguration.PolicyCacheMinutes"/>.</summary>
    public int? PolicyCacheMinutes { get; set; }

    /// <summary>Gets or sets <see cref="PluginConfiguration.NewServerLibraryMode"/>.</summary>
    public LibraryMode? NewServerLibraryMode { get; set; }

    /// <summary>
    /// Takes every setting of a configuration, so that applying it puts them back.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The settings, all set.</returns>
    public static SettingsRequest From(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new SettingsRequest
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
            NewServerLibraryMode = config.NewServerLibraryMode
        };
    }

    /// <summary>
    /// Sets the settings this request holds on <paramref name="config"/>, leaving the others.
    /// </summary>
    /// <param name="config">The configuration to change.</param>
    public void ApplyTo(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.SyncIntervalSeconds = SyncIntervalSeconds ?? config.SyncIntervalSeconds;
        config.ReconciliationTime = ReconciliationTime ?? config.ReconciliationTime;
        config.FederatedFolderPath = FederatedFolderPath?.Trim() ?? config.FederatedFolderPath;
        config.MetadataProtection = MetadataProtection ?? config.MetadataProtection;
        config.FederateCollections = FederateCollections ?? config.FederateCollections;
        config.CloseTranscodeAtSlotExpiry = CloseTranscodeAtSlotExpiry ?? config.CloseTranscodeAtSlotExpiry;
        config.RemotePlaybackInfoTimeoutSeconds = RemotePlaybackInfoTimeoutSeconds ?? config.RemotePlaybackInfoTimeoutSeconds;
        config.StreamOpenTimeoutSeconds = StreamOpenTimeoutSeconds ?? config.StreamOpenTimeoutSeconds;
        config.PlaybackIdleSeconds = PlaybackIdleSeconds ?? config.PlaybackIdleSeconds;
        config.SessionRetentionHours = SessionRetentionHours ?? config.SessionRetentionHours;
        config.PolicyCacheMinutes = PolicyCacheMinutes ?? config.PolicyCacheMinutes;
        config.NewServerLibraryMode = NewServerLibraryMode ?? config.NewServerLibraryMode;
    }
}
