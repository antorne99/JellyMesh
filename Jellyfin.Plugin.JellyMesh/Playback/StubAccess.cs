namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// What a stock route of a stub wants of its user, beyond being signed in and seeing every stub it names (row 458).
/// </summary>
internal enum StubAccess
{
    /// <summary>
    /// Nothing more.
    /// </summary>
    Visible,

    /// <summary>
    /// The download permission of Jellyfin as well (D12).
    /// </summary>
    Download
}
