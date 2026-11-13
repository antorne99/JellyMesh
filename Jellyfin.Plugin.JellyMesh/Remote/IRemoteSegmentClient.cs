using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// Reads the media segments of a remote item with the official <c>GET /MediaSegments/{itemId}</c>.
/// </summary>
public interface IRemoteSegmentClient
{
    /// <summary>
    /// Reads the segments of an item. A segment of an unknown type or with impossible times is left out. A client error
    /// other than 401 and 403, such as not found, refuses the item alone and is no failure.
    /// </summary>
    /// <param name="endpoint">The remote.</param>
    /// <param name="session">The session.</param>
    /// <param name="itemId">The item on the remote.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The segments, empty when the remote has none or refused the item.</returns>
    Task<RemoteResult<RemoteSegmentRead>> GetSegmentsAsync(RemoteEndpoint endpoint, RemoteSession session, string itemId, CancellationToken cancellationToken);
}
