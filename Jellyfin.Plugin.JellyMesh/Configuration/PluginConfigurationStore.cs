using System;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// <see cref="IPluginConfigurationStore"/> backed by the plugin instance.
/// </summary>
public sealed class PluginConfigurationStore : IPluginConfigurationStore
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfigurationStore"/> class.
    /// </summary>
    public PluginConfigurationStore()
    {
        Plugin.ConfigurationChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler? SettingsChanged;

    private static Plugin Plugin => Plugin.Instance
        ?? throw new InvalidOperationException("The JellyMesh plugin is not initialized.");

    /// <inheritdoc />
    public T Read<T>(Func<PluginConfiguration, T> read) => Plugin.ReadConfiguration(read);

    /// <inheritdoc />
    public void Update(Action<PluginConfiguration> update) => Plugin.ChangeConfiguration(update);
}
