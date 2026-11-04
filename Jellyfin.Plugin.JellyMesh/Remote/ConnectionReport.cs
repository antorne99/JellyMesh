using System;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// Result of a connection test. Holds nothing secret.
/// </summary>
/// <param name="Capabilities">The capabilities. <see cref="RemoteMode.Unknown"/> if detection failed.</param>
/// <param name="LibraryCount">The number of importable libraries.</param>
public sealed record ConnectionReport(RemoteCapabilities Capabilities, int LibraryCount);
