using Jellyfin.Plugin.JellyMesh.Library;

namespace Jellyfin.Plugin.JellyMesh.Segments;

/// <summary>
/// The source the segments of a stub were read from, with its duration.
/// </summary>
/// <param name="Source">The remote source.</param>
/// <param name="RunTimeTicks">Its duration as the index knows it, null when unknown.</param>
public sealed record SegmentOrigin(StubSource Source, long? RunTimeTicks);
