using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The files of one season folder: its <c>season.nfo</c> and its poster (decision 50).
/// </summary>
/// <param name="Entry">The season as written before, or a new one.</param>
/// <param name="Metadata">The name, plot and number.</param>
/// <param name="Nfo">The text of the <c>season.nfo</c>.</param>
/// <param name="NfoPath">The relative path of the <c>season.nfo</c>.</param>
/// <param name="ContentHash">The hash of what is to be written.</param>
/// <param name="Images">The poster, if the remote season has one.</param>
/// <param name="ImageTag">The tag of the images.</param>
public sealed record SeasonWork(
    SeasonStub Entry,
    StubMetadata Metadata,
    string Nfo,
    string NfoPath,
    string ContentHash,
    IReadOnlyList<StubImage> Images,
    string ImageTag);
