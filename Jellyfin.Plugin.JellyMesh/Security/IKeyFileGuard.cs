using System.IO;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// Keeps a key file readable only by the account running Jellyfin, in the way of the host system.
/// </summary>
internal interface IKeyFileGuard
{
    /// <summary>
    /// Creates a new file that is private from its first byte.
    /// </summary>
    /// <param name="path">The file, which must not exist.</param>
    /// <returns>A stream to write the file.</returns>
    FileStream CreatePrivate(string path);

    /// <summary>
    /// Makes an existing file private again, such as one restored from a backup.
    /// </summary>
    /// <param name="path">The file.</param>
    void KeepPrivate(string path);
}
