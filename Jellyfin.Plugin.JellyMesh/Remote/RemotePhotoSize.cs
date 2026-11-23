namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// The size and quality a client asks of the image of a photo. Values that are not positive are ignored.
/// </summary>
public sealed record RemotePhotoSize
{
    /// <summary>Gets the largest width.</summary>
    public int? MaxWidth { get; init; }

    /// <summary>Gets the largest height.</summary>
    public int? MaxHeight { get; init; }

    /// <summary>Gets the exact width.</summary>
    public int? Width { get; init; }

    /// <summary>Gets the exact height.</summary>
    public int? Height { get; init; }

    /// <summary>Gets the width of the box to fill.</summary>
    public int? FillWidth { get; init; }

    /// <summary>Gets the height of the box to fill.</summary>
    public int? FillHeight { get; init; }

    /// <summary>Gets the JPEG quality.</summary>
    public int? Quality { get; init; }
}
