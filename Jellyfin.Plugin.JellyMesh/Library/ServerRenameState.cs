namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What the page shows about the rename of a server.
/// </summary>
/// <param name="Busy">Whether the rename is running.</param>
/// <param name="Pending">Whether stubs of the server still wait for a scan to finish their move.</param>
/// <param name="Failed">Whether the last rename did not finish, kept across a restart (§10 row 289).</param>
public sealed record ServerRenameState(bool Busy, bool Pending, bool Failed)
{
    /// <summary>
    /// Gets a value indicating whether the rename stays pending because an old item keeps failing to leave its version
    /// group: it is retried after every scan (§10 row 359).
    /// </summary>
    public bool Stuck { get; init; }
}
