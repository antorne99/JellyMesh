using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Index;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// The state of the sync, whichever started it.
/// </summary>
/// <param name="IsRunning">Whether a sync is running.</param>
/// <param name="Health">The state of the index file.</param>
/// <param name="IndexSaveFailed">Whether the last sync could not save what it read.</param>
/// <param name="LastStartedUtc">When the last sync started.</param>
/// <param name="LastFinishedUtc">When the last sync ended.</param>
/// <param name="Servers">The configured servers.</param>
public sealed record SyncStatus(
    bool IsRunning,
    IndexHealth Health,
    bool IndexSaveFailed,
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastFinishedUtc,
    IReadOnlyList<ServerSyncReport> Servers);
