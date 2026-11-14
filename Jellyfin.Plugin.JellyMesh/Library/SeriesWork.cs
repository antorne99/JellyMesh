using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The files of one series stub.
/// </summary>
/// <param name="Entry">The stub.</param>
/// <param name="IsNew">Whether it was minted by this plan.</param>
/// <param name="Metadata">The metadata.</param>
/// <param name="Nfo">The text of the <c>tvshow.nfo</c>.</param>
/// <param name="NfoPath">The relative path of the <c>tvshow.nfo</c>.</param>
/// <param name="ContentHash">The hash of what is to be written.</param>
/// <param name="Images">The images.</param>
/// <param name="ImageTag">The tag of the images.</param>
public sealed record SeriesWork(
    SeriesStub Entry,
    bool IsNew,
    StubMetadata Metadata,
    string Nfo,
    string NfoPath,
    string ContentHash,
    IReadOnlyList<StubImage> Images,
    string ImageTag)
{
    /// <summary>Gets the season folders of the episodes that have work, each with its own files.</summary>
    public IReadOnlyList<SeasonWork> Seasons { get; init; } = [];

    /// <summary>Gets the images written before whose type the remote row no longer has, to be deleted (Q235).</summary>
    public IReadOnlyList<StubImage> RemovedImages { get; init; } = [];
}
