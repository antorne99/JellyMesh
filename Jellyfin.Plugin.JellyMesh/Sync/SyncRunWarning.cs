using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// What one sync of a server should tell the admin without having failed. Holds no address.
/// </summary>
/// <param name="VanishedLibraries">The names of the remote libraries that left the imported ones in this sync. Untrusted text.</param>
public sealed record SyncRunWarning(IReadOnlyList<string> VanishedLibraries);
