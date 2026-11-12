using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Activity;

/// <summary>
/// One entry of the activity log, written for no user.
/// </summary>
/// <param name="Name">The line shown in the log.</param>
/// <param name="Type">The kind of entry, the same for every entry of that kind.</param>
/// <param name="Severity">How serious it is.</param>
public sealed record ActivityEntry(string Name, string Type, LogLevel Severity)
{
    /// <summary>
    /// Gets the short detail shown under the line, if any.
    /// </summary>
    public string? ShortOverview { get; init; }

    /// <summary>
    /// Gets the full detail, if any.
    /// </summary>
    public string? Overview { get; init; }
}
