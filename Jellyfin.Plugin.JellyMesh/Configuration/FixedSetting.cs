namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// A setting an environment variable decides, so the page cannot change it.
/// </summary>
/// <param name="Field">The setting, as named in <see cref="PluginSettings"/>.</param>
/// <param name="Variable">The environment variable.</param>
public sealed record FixedSetting(string Field, string Variable);
