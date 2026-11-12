namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What a run of the watch state carry did.
/// </summary>
/// <param name="Carried">The moves done for every user.</param>
/// <param name="Skipped">
/// The moves not done: an item not found, versions not linked as one title, or a primary not among them. A later run
/// does them only once the cause is gone; versions that are never linked, such as episodes of different ranges (D3),
/// stay skipped.
/// </param>
public sealed record WatchStateCarryReport(int Carried, int Skipped);
