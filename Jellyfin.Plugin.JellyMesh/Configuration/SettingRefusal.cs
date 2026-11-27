namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Why a save of the settings was refused, naming the field; nothing was saved.
/// </summary>
/// <param name="Field">The setting, as named in <see cref="PluginSettings"/>.</param>
/// <param name="Message">What is wrong with its value.</param>
public sealed record SettingRefusal(string Field, string Message);
