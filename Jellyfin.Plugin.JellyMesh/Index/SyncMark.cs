using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// Where the next incremental read of a server starts from.
/// </summary>
/// <param name="ReadStartedUtc">When the read began, by the clock of the remote, which is the clock of what it filters on.</param>
/// <param name="LibraryIds">The libraries that read covered. If the choice changes, only a full read can catch up.</param>
public sealed record SyncMark(DateTimeOffset ReadStartedUtc, IReadOnlyList<string> LibraryIds);
