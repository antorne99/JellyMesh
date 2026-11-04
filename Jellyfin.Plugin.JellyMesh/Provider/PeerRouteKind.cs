namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// How a request of a peer is treated.
/// </summary>
public enum PeerRouteKind
{
    /// <summary>
    /// Not for a peer.
    /// </summary>
    Closed,

    /// <summary>
    /// Allowed, with nothing to check here.
    /// </summary>
    Open,

    /// <summary>
    /// Allowed on an item the peer may reach.
    /// </summary>
    Item,

    /// <summary>
    /// A <c>PlaybackInfo</c>, which takes a slot.
    /// </summary>
    PlaybackInfo,

    /// <summary>
    /// A stream, which needs a slot.
    /// </summary>
    Stream
}
