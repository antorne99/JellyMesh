using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// What the configuration page shows of the sync history.
/// </summary>
/// <param name="Runs">The runs, newest first.</param>
/// <param name="IsDamaged">Whether earlier runs could not be read.</param>
public sealed record SyncHistoryView(IReadOnlyList<SyncRun> Runs, bool IsDamaged);
