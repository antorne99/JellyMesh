namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// The item types read from a remote catalog. Names are the <c>includeItemTypes</c> values of <c>/Items</c>.
/// </summary>
public enum RemoteItemType
{
    /// <summary>A film.</summary>
    Movie,

    /// <summary>A series.</summary>
    Series,

    /// <summary>A season.</summary>
    Season,

    /// <summary>An episode.</summary>
    Episode
}
