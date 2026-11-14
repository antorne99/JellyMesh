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

    /// <summary>
    /// Gets the generation of the stub registry when the run ended (Q386).
    /// </summary>
    public long RegistryGeneration { get; init; }

    /// <summary>
    /// Gets how many local titles wait for their first refresh, whose wait ends with time (Q243).
    /// </summary>
    public int AwaitingRefresh { get; init; }

    /// <summary>
    /// Gets how many images could not be written. They are tried again at the next run.
    /// </summary>
    public int ImagesPending { get; init; }

    /// <summary>
    /// Gets how many folders of removed servers could not be deleted. They are tried again at the next run.
    /// </summary>
    public int FoldersPending { get; init; }

    /// <summary>
    /// Gets a value indicating whether the run asked Jellyfin for a scan of the libraries.
    /// </summary>
    public bool ScanQueued { get; init; }
}
