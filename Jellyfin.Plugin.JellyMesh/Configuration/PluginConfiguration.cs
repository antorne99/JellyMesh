using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
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

    private static readonly SettingRange[] _ranges =
    [
        new(nameof(SyncIntervalSeconds), config => config.SyncIntervalSeconds, (config, value) => config.SyncIntervalSeconds = value, MinSyncIntervalSeconds, int.MaxValue)
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
    /// Gets or sets the user-chosen federated folder. Empty until chosen.
    /// </summary>
    public string FederatedFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether federated items are locked against metadata fetchers.
    /// </summary>
    public bool MetadataProtection { get; set; } = true;

    /// <summary>
    /// Gets the configured remote servers. Never sent to the browser through this object.
    /// </summary>
    [JsonIgnore]
    public Collection<RemoteServerConfig> Servers { get; } = [];

    /// <summary>
    /// Gets the JellyMesh servers connected to this one. Never sent to the browser through this object.
    /// </summary>
    [JsonIgnore]
    public Collection<ProviderPeerConfig> Peers { get; } = [];

    /// <summary>
    /// Replaces <see cref="Servers"/> and <see cref="Peers"/> with those of <paramref name="current"/>. The stock save
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

        Peers.Clear();
        foreach (var peer in current.Peers)
        {
            Peers.Add(peer);
        }
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
