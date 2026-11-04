using System;
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
    /// Gets or sets the incremental sync interval in seconds.
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
    /// Replaces <see cref="Servers"/> with those of <paramref name="current"/>. The stock save
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
    }

    /// <summary>
    /// Validates the user-editable settings.
    /// </summary>
    /// <exception cref="ArgumentException">A setting is invalid. Jellyfin answers such a save with 400.</exception>
    public void Validate()
    {
        if (SyncIntervalSeconds <= 0)
        {
            throw new ArgumentException("The sync interval must be a positive number of seconds.", nameof(SyncIntervalSeconds));
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

    private static bool IsUsableFolder(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Split('/', '\\').Contains(".."))
        {
            return false;
        }

        return Path.GetDirectoryName(path.TrimEnd('/', '\\')) is not null;
    }
}
