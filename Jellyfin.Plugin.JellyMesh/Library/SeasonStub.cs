namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The files of a season folder of a stub series (decision 50). The folder goes with its last episode stub.
/// </summary>
public sealed record SeasonStub
{
    /// <summary>Gets the season folder relative to the federated folder.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Gets a hash of the <c>season.nfo</c> written, null until it is on disk.</summary>
    public string? ContentHash { get; init; }

    /// <summary>Gets a tag of the poster written, null until it is on disk.</summary>
    public string? ImageTag { get; init; }
}
