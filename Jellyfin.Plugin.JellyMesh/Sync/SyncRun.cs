using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// One sync in the history (decision 48). Holds no address.
/// </summary>
/// <param name="StartedUtc">When it started.</param>
/// <param name="DurationMilliseconds">How long it took.</param>
/// <param name="Kind">What it read.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Servers">The servers it read.</param>
public sealed record SyncRun(
    DateTimeOffset StartedUtc,
    long DurationMilliseconds,
    SyncRunKind Kind,
    SyncRunOutcome Outcome,
    IReadOnlyList<SyncRunServer> Servers)
{
    /// <summary>Gets how many stubs were created in the whole federated folder.</summary>
    public int Created { get; init; }

    /// <summary>Gets how many stubs were updated in the whole federated folder.</summary>
    public int Updated { get; init; }

    /// <summary>Gets how many stubs were removed from the whole federated folder.</summary>
    public int Removed { get; init; }

    /// <summary>Gets how many stubs could not be written or removed in the whole federated folder.</summary>
    public int Failed { get; init; }

    /// <summary>Gets a value indicating whether what was read could not be saved in the index.</summary>
    public bool IndexSaveFailed { get; init; }

    /// <summary>Gets a value indicating whether the federated folder could not be brought in step.</summary>
    public bool MaterializationFailed { get; init; }
}
