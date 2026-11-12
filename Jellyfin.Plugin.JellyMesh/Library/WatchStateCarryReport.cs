namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What a run of the watch state carry did.
/// </summary>
/// <param name="Carried">The moves done for every user.</param>
/// <param name="Skipped">The moves left for a later run: an item not found, or versions not linked yet.</param>
public sealed record WatchStateCarryReport(int Carried, int Skipped);
