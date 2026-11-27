using System;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Serialized access to the plugin configuration. Callers must not keep what they read past the
/// callback: <see cref="RemoteServerConfig"/> instances are shared and mutable.
/// </summary>
public interface IPluginConfigurationStore
{
    /// <summary>
    /// Occurs after a change of the settings was saved, on the saving thread; not for servers, peers or other state.
    /// </summary>
    event EventHandler? SettingsChanged;

    /// <summary>
    /// Reads from the configuration.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="read">Projects the configuration to a detached result.</param>
    /// <returns>The result of <paramref name="read"/>.</returns>
    /// <exception cref="Storage.StoreSuspendedException">The configuration cannot be read yet: callers fail closed, never on defaults.</exception>
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
