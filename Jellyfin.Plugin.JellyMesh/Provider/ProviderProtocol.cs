namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// What two JellyMesh servers agree on. Consumer and provider both read these values.
/// </summary>
public static class ProviderProtocol
{
    /// <summary>
    /// The protocol version answered on the capability endpoint.
    /// </summary>
    public const int Version = 2;

    /// <summary>
    /// The first protocol version whose provider serves the collections route (decision 230). Older peers answer 404.
    /// </summary>
    public const int CollectionsVersion = 2;

    /// <summary>
    /// The route of the ProviderApi.
    /// </summary>
    public const string Route = "JellyMesh/Provider";

    /// <summary>
    /// The response header that marks a refusal of the ProviderApi. A stock server never sends it, which is what makes
    /// "limit reached" a JellyMesh-only signal (decision 8).
    /// </summary>
    public const string RefusalHeader = "X-JellyMesh-Refusal";

    /// <summary>
    /// The value of <see cref="RefusalHeader"/> when the streams of a peer are at their limit.
    /// </summary>
    public const string StreamLimitRefusal = "stream-limit";

    /// <summary>
    /// The request header naming the play session a new <c>PlaybackInfo</c> replaces, so that changing the audio track
    /// on a full server hands the slot over instead of being refused.
    /// </summary>
    public const string ReplacesHeader = "X-JellyMesh-Replaces";

    /// <summary>
    /// The request header marking a stream request as a probe of the first bytes. The consumer drops the connection of a
    /// probe on purpose, and that must not read as a client that left.
    /// </summary>
    public const string ProbeHeader = "X-JellyMesh-Probe";

    /// <summary>
    /// The most items in a page of the catalog, and the most identifiers in one lookup.
    /// </summary>
    public const int MaxItemsPerPage = 500;

    /// <summary>
    /// The most changes in a page of the feed.
    /// </summary>
    public const int MaxChangesPerPage = 1000;

    /// <summary>
    /// The most reads of a peer served at a time: pages of the catalog, images and subtitles. A consumer syncs one call at a
    /// time and its clients read a subtitle now and then, so this only stops a peer that asks for everything at once.
    /// Streams are bounded by their slots instead.
    /// </summary>
    public const int MaxReadsPerPeer = 8;
}
