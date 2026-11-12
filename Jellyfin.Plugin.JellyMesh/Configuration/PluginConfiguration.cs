using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Provider;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Plugin configuration, persisted by Jellyfin as XML.
/// </summary>
/// <remarks>
/// The stock <c>/Plugins/{id}/Configuration</c> endpoints exchange this object as JSON with the
/// browser, so <see cref="Servers"/> is excluded from JSON and survives only through
/// <see cref="AdoptServersFrom"/>. XML serialization does not honor <see cref="JsonIgnoreAttribute"/>.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Format of <see cref="ReconciliationTime"/>.
    /// </summary>
    public const string ReconciliationTimeFormat = "HH:mm";

    /// <summary>
    /// The lowest sync interval: the overlap of the incremental sync, so that two runs never read past each other
    /// (decision 15).
    /// </summary>
    public const int MinSyncIntervalSeconds = 300;

    /// <summary>
    /// The highest idle time of a playback. A JellyMesh provider must not see a slot expire while this server still holds
    /// the stream, so the idle time plus a sweep stays below the slot expiry (<see cref="StreamSlots.IdleTimeout"/>).
    /// </summary>
    public static readonly int MaxPlaybackIdleSeconds = (int)(StreamSlots.IdleTimeout - PlaybackTimeouts.SweepInterval).TotalSeconds - 1;

    private static readonly SettingRange[] _ranges =
    [
        new(nameof(SyncIntervalSeconds), config => config.SyncIntervalSeconds, (config, value) => config.SyncIntervalSeconds = value, MinSyncIntervalSeconds, int.MaxValue),
        new(nameof(RemotePlaybackInfoTimeoutSeconds), config => config.RemotePlaybackInfoTimeoutSeconds, (config, value) => config.RemotePlaybackInfoTimeoutSeconds = value, 3, 60),
        new(nameof(StreamOpenTimeoutSeconds), config => config.StreamOpenTimeoutSeconds, (config, value) => config.StreamOpenTimeoutSeconds = value, 5, 60),
        new(nameof(PlaybackIdleSeconds), config => config.PlaybackIdleSeconds, (config, value) => config.PlaybackIdleSeconds = value, 30, MaxPlaybackIdleSeconds),
        new(nameof(SessionRetentionHours), config => config.SessionRetentionHours, (config, value) => config.SessionRetentionHours = value, 1, 168),
        new(nameof(PolicyCacheMinutes), config => config.PolicyCacheMinutes, (config, value) => config.PolicyCacheMinutes = value, 1, 60)
    ];

    /// <summary>
    /// Gets or sets the incremental sync interval in seconds, at least <see cref="MinSyncIntervalSeconds"/>.
    /// </summary>
    public int SyncIntervalSeconds { get; set; } = 900;

    /// <summary>
    /// Gets or sets the local time of the nightly reconciliation, as <c>HH:mm</c>.
    /// </summary>
    public string ReconciliationTime { get; set; } = "03:00";

    /// <summary>
    /// Gets or sets the federated folder, chosen by the user or set by <c>JELLYMESH_FEDERATED_ROOT</c>. Empty until chosen.
    /// </summary>
    public string FederatedFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether federated items are locked against metadata fetchers.
    /// </summary>
    public bool MetadataProtection { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a stream slot that expires without a lost connection closes its transcode.
    /// When off that is left to the timer of stock. A lost connection closes the transcode either way.
    /// </summary>
    public bool CloseTranscodeAtSlotExpiry { get; set; } = true;

    /// <summary>
    /// Gets or sets the limit in seconds on the <c>PlaybackInfo</c> call to a remote.
    /// </summary>
    public int RemotePlaybackInfoTimeoutSeconds { get; set; } = (int)PlaybackTimeouts.RemotePlaybackInfo.TotalSeconds;

    /// <summary>
    /// Gets or sets the limit in seconds on each step of opening a stream: a playlist, a segment or the first byte of a file.
    /// </summary>
    public int StreamOpenTimeoutSeconds { get; set; } = (int)PlaybackTimeouts.StreamOpen.TotalSeconds;

    /// <summary>
    /// Gets or sets the seconds without requests after which the transcode of a session is closed, at most
    /// <see cref="MaxPlaybackIdleSeconds"/>.
    /// </summary>
    public int PlaybackIdleSeconds { get; set; } = (int)PlaybackTimeouts.Idle.TotalSeconds;

    /// <summary>
    /// Gets or sets the hours without requests after which a session is forgotten, and a paused player can no longer resume.
    /// </summary>
    public int SessionRetentionHours { get; set; } = (int)PlaybackTimeouts.SessionRetention.TotalHours;

    /// <summary>
    /// Gets or sets the minutes the transcode policy of a remote is kept.
    /// </summary>
    public int PolicyCacheMinutes { get; set; } = (int)PlaybackTimeouts.PolicyRetention.TotalMinutes;

    /// <summary>
    /// Gets the configured remote servers. Never sent to the browser through this object.
    /// </summary>
    [JsonIgnore]
    public Collection<RemoteServerConfig> Servers { get; } = [];

    /// <summary>
    /// Gets the servers removed from the page whose Server Id was known, so that one added again gets its identifier back
    /// (decision 116). Never sent to the browser through this object.
    /// </summary>
    [JsonIgnore]
    public Collection<FormerServerConfig> FormerServers { get; } = [];

    /// <summary>
    /// Gets the JellyMesh servers connected to this one. Never sent to the browser through this object.
    /// </summary>
    [JsonIgnore]
    public Collection<ProviderPeerConfig> Peers { get; } = [];

    /// <summary>
    /// Replaces <see cref="Servers"/>, <see cref="FormerServers"/> and <see cref="Peers"/> with those of <paramref name="current"/>. The stock save
    /// endpoint deserializes JSON that never holds them, so they would otherwise be erased.
    /// </summary>
    /// <param name="current">The configuration in force.</param>
    public void AdoptServersFrom(PluginConfiguration current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (ReferenceEquals(this, current))
        {
            return;
        }

        Servers.Clear();
        foreach (var server in current.Servers)
        {
            Servers.Add(server);
        }

        FormerServers.Clear();
        foreach (var former in current.FormerServers)
        {
            FormerServers.Add(former);
        }

        Peers.Clear();
        foreach (var peer in current.Peers)
        {
            Peers.Add(peer);
        }
    }

    /// <summary>
    /// Reads a federated folder set outside the configuration page.
    /// </summary>
    /// <param name="value">The value as set.</param>
    /// <returns>The folder, or <c>null</c> if the value is blank or not a usable folder.</returns>
    internal static string? FederatedRootOf(string? value)
    {
        var path = value?.Trim();
        return string.IsNullOrEmpty(path) || !IsUsableFolder(path) ? null : path;
    }

    /// <summary>
    /// Validates the user-editable settings.
    /// </summary>
    /// <exception cref="ArgumentException">A setting is invalid. Jellyfin answers such a save with 400.</exception>
    public void Validate()
    {
        foreach (var range in _ranges)
        {
            if (!range.Holds(this))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"{range.Name} must be between {range.Min} and {range.Max}."),
                    range.Name);
            }
        }

        if (!TimeOnly.TryParseExact(ReconciliationTime, ReconciliationTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new ArgumentException("The reconciliation time must use the HH:mm format.", nameof(ReconciliationTime));
        }

        if (FederatedFolderPath.Length > 0 && !IsUsableFolder(FederatedFolderPath))
        {
            throw new ArgumentException("The federated folder must be an absolute path other than the filesystem root.", nameof(FederatedFolderPath));
        }
    }

    /// <summary>
    /// Brings a loaded configuration back into its ranges. A value saved before a range existed must not stop the server
    /// from working, so it runs as the nearest allowed value, here and on the configuration page.
    /// </summary>
    /// <returns>What was changed, for the log.</returns>
    public IReadOnlyList<CorrectedSetting> Normalize()
    {
        var corrected = new List<CorrectedSetting>();
        foreach (var range in _ranges.Where(range => !range.Holds(this)))
        {
            var saved = range.Read(this);
            range.Write(this, Math.Clamp(saved, range.Min, range.Max));
            corrected.Add(new CorrectedSetting(range.Name, saved, range.Read(this)));
        }

        return corrected;
    }

    private static bool IsUsableFolder(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Split('/', '\\').Contains(".."))
        {
            return false;
        }

        return Path.GetDirectoryName(path.TrimEnd('/', '\\')) is not null;
    }

    private sealed record SettingRange(string Name, Func<PluginConfiguration, int> Read, Action<PluginConfiguration, int> Write, int Min, int Max)
    {
        public bool Holds(PluginConfiguration configuration) => Read(configuration) >= Min && Read(configuration) <= Max;
    }
}
