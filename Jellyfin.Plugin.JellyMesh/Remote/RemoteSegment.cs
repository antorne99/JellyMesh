using Jellyfin.Database.Implementations.Enums;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// A media segment of a remote item, such as an intro or the end credits. Holds no identifier of the remote.
/// </summary>
/// <param name="Type">The kind of segment.</param>
/// <param name="StartTicks">Where it starts.</param>
/// <param name="EndTicks">Where it ends, after the start.</param>
public sealed record RemoteSegment(MediaSegmentType Type, long StartTicks, long EndTicks);
