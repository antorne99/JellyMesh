using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The outcome of a run.
/// </summary>
/// <param name="FinishedUtc">When it ended.</param>
/// <param name="IsFolderSet">Whether a federated folder is configured. If not, nothing was done.</param>
/// <param name="Created">How many stubs were created.</param>
/// <param name="Updated">How many stubs were updated.</param>
/// <param name="Removed">How many stubs were removed because their title is gone.</param>
/// <param name="Failed">How many stubs could not be written. They are tried again at the next run.</param>
/// <param name="Unplaceable">How many episodes have no season or number.</param>
/// <param name="Held">The libraries that came back empty, whose stubs were kept.</param>
public sealed record MaterializationReport(
    DateTimeOffset FinishedUtc,
    bool IsFolderSet,
    int Created,
    int Updated,
    int Removed,
    int Failed,
    int Unplaceable,
    IReadOnlyList<HeldLibrary> Held)
{
    /// <summary>
    /// Gets how many stubs are kept because none of their servers is configured and none was removed from the page.
    /// </summary>
    public int Unconfigured { get; init; }

    /// <summary>
    /// Gets the counts of each server's folder, for the servers whose stubs the run touched. Stubs of the flat layout are
    /// only in the totals.
    /// </summary>
    public IReadOnlyDictionary<Guid, StubCounts> ByServer { get; init; } = new Dictionary<Guid, StubCounts>();
}
