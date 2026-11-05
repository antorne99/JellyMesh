using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// What a film copy says about itself besides title and year, for confirming a match between copies when IDs cannot
/// (decision 13). Everything can be missing.
/// </summary>
public sealed record ComparableMetadata
{
    /// <summary>
    /// Gets an instance with nothing in it.
    /// </summary>
    public static ComparableMetadata None { get; } = new();

    /// <summary>Gets the original title.</summary>
    public string? OriginalTitle { get; init; }

    /// <summary>Gets the duration.</summary>
    public long? RunTimeTicks { get; init; }

    /// <summary>Gets the plot.</summary>
    public string? Overview { get; init; }

    /// <summary>Gets the genres.</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Gets the studios.</summary>
    public IReadOnlyList<string> Studios { get; init; } = [];

    /// <summary>
    /// Gets the names of cast and crew. Lazy because a local film needs one database read for them, and only the few
    /// films that have to be compared ever use it.
    /// </summary>
    public Lazy<IReadOnlyList<string>> People { get; init; } = new(() => []);
}
