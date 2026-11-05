using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The files of one film or episode stub.
/// </summary>
/// <param name="Entry">The stub.</param>
/// <param name="IsNew">Whether it was minted by this plan.</param>
/// <param name="Metadata">The metadata.</param>
/// <param name="Nfo">The text of the <c>.nfo</c>.</param>
/// <param name="NfoPath">The relative path of the <c>.nfo</c>.</param>
/// <param name="StrmUrl">The content of the <c>.strm</c>.</param>
/// <param name="ContentHash">The hash of what is to be written.</param>
/// <param name="Images">The images.</param>
/// <param name="ImageTag">The tag of the images.</param>
/// <param name="TechnicalTag">The tag of the technical data.</param>
public sealed record StubWork(
    StubEntry Entry,
    bool IsNew,
    StubMetadata Metadata,
    string Nfo,
    string NfoPath,
    string StrmUrl,
    string ContentHash,
    IReadOnlyList<StubImage> Images,
    string ImageTag,
    string TechnicalTag);
