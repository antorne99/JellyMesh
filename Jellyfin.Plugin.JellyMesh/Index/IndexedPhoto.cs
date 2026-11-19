namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// The size and the camera data of a photo. The place where it was taken is never kept. Text values are untrusted.
/// </summary>
public sealed record IndexedPhoto
{
    /// <summary>Gets the width.</summary>
    public int? Width { get; init; }

    /// <summary>Gets the height.</summary>
    public int? Height { get; init; }

    /// <summary>Gets the camera maker.</summary>
    public string? CameraMake { get; init; }

    /// <summary>Gets the camera model.</summary>
    public string? CameraModel { get; init; }

    /// <summary>Gets the software that wrote it.</summary>
    public string? Software { get; init; }

    /// <summary>Gets the exposure time, in seconds.</summary>
    public double? ExposureTime { get; init; }

    /// <summary>Gets the focal length.</summary>
    public double? FocalLength { get; init; }

    /// <summary>Gets the aperture.</summary>
    public double? Aperture { get; init; }

    /// <summary>Gets the shutter speed.</summary>
    public double? ShutterSpeed { get; init; }

    /// <summary>Gets the orientation.</summary>
    public string? ImageOrientation { get; init; }

    /// <summary>Gets the ISO speed.</summary>
    public int? IsoSpeedRating { get; init; }
}
