using System;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// Picks the way of keeping a file private on this system, for the key file and the other files only the owner may read.
/// </summary>
internal static class KeyFileGuards
{
    /// <summary>
    /// Gives the access list on Windows and the owner-only mode elsewhere.
    /// </summary>
    /// <returns>The guard of this system.</returns>
    public static IKeyFileGuard ForThisSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsKeyFileGuard();
        }

        return new UnixKeyFileGuard();
    }
}
