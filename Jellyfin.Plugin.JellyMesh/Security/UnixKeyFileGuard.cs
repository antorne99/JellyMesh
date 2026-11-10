using System.IO;
using System.Runtime.Versioning;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// Keeps a key file readable and writable only by its owner, with Unix file modes.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class UnixKeyFileGuard : IKeyFileGuard
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <inheritdoc />
    public FileStream CreatePrivate(string path)
    {
        return new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = Private });
    }

    /// <inheritdoc />
    public void KeepPrivate(string path)
    {
        if (File.GetUnixFileMode(path) != Private)
        {
            File.SetUnixFileMode(path, Private);
        }
    }
}
