using System;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Serialized access to the plugin configuration. Callers must not keep what they read past the
/// callback: <see cref="RemoteServerConfig"/> instances are shared and mutable.
/// </summary>
public interface IPluginConfigurationStore
{
    /// <summary>
    /// Occurs after the settings were saved through the stock plugin endpoint, on the saving thread.
    /// </summary>
    event EventHandler? SettingsChanged;

    /// <summary>
    /// Reads from the configuration.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="read">Projects the configuration to a detached result.</param>
    /// <returns>The result of <paramref name="read"/>.</returns>
    T Read<T>(Func<PluginConfiguration, T> read);

    /// <summary>
    /// Changes the configuration and saves it.
    /// </summary>
    /// <param name="update">The change to apply.</param>
    void Update(Action<PluginConfiguration> update);

    /// <summary>
    /// Changes the configuration and saves it; if that fails, undoes the change before any other reader sees it.
    /// </summary>
    /// <param name="update">The change to apply.</param>
    /// <param name="rollback">Puts the configuration back as saved, run under the same lock when the change or its save throws.</param>
    void Update(Action<PluginConfiguration> update, Action<PluginConfiguration> rollback);
}
