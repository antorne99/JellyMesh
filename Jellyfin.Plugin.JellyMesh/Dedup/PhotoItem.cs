namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// A photo with what its identity is read from (row 565).
/// </summary>
public sealed record PhotoItem : IServerItem
{
    /// <summary>Gets the plugin's ID for the server holding the photo.</summary>
    public required string ServerId { get; init; }

    /// <summary>Gets the item ID on that server.</summary>
    public required string ItemId { get; init; }

    /// <summary>Gets the title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the year.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the name of the folder that holds the photo, null for one in no folder of its library.</summary>
    public string? Folder { get; init; }
}
