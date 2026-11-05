namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// A saved setting that was out of its range, and the value it runs with.
/// </summary>
/// <param name="Name">The setting.</param>
/// <param name="Saved">The saved value.</param>
/// <param name="Applied">The value in force.</param>
public sealed record CorrectedSetting(string Name, int Saved, int Applied);
