namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// The state of the plugin database, which sync and remote playback read to know if they must stay suspended.
/// </summary>
/// <param name="State">The state.</param>
/// <param name="Reason">Why the database is not healthy, for the page; <c>null</c> when it is.</param>
public sealed record StoreHealth(StoreState State, string? Reason = null)
{
    /// <summary>Gets the health of an open, current database.</summary>
    public static StoreHealth Healthy { get; } = new(StoreState.Healthy);

    /// <summary>Gets a value indicating whether sync and remote playback must stay suspended.</summary>
    public bool Suspended => State != StoreState.Healthy;
}
