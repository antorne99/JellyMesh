namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// One playable version of an item: one entry of <c>BaseItemDto.MediaSources</c>.
/// A title with several versions on one server is one item with several of these.
/// </summary>
/// <param name="MediaSourceId">The <c>MediaSourceInfo.Id</c>. Not unique across servers.</param>
/// <param name="Quality">The quality of this version.</param>
public sealed record MediaVersion(string MediaSourceId, SourceQuality Quality);
