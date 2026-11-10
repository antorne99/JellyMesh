using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// Gives a key file the explicit access list of <see cref="KeyFileAcl"/> instead of the permissions of its folder.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsKeyFileGuard : IKeyFileGuard
{
    // FILE_GENERIC_WRITE, which the flush to disk asks of the handle.
    private const FileSystemRights GenericWrite = FileSystemRights.Write | FileSystemRights.Synchronize | FileSystemRights.ReadPermissions;

    /// <inheritdoc />
    public FileStream CreatePrivate(string path)
    {
        return new FileInfo(path).Create(FileMode.CreateNew, GenericWrite, FileShare.None, 4096, FileOptions.None, KeyFileAcl.ForThisProcess());
    }

    /// <inheritdoc />
    public void KeepPrivate(string path)
    {
        new FileInfo(path).SetAccessControl(KeyFileAcl.ForThisProcess());
    }
}
