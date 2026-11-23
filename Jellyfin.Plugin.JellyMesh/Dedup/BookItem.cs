using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// An ebook, PDF or comic with what its identity is read from (row 535).
/// </summary>
public sealed record BookItem
{
    /// <summary>Gets the plugin's ID for the server holding the book.</summary>
    public required string ServerId { get; init; }

    /// <summary>Gets the item ID on that server.</summary>
    public required string ItemId { get; init; }

    /// <summary>Gets the title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the year.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the series.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Gets the file extension.</summary>
    public string? Extension { get; init; }

    /// <summary>Gets the provider IDs, without the mark of the plugin.</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();
}
