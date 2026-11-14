using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// An immutable copy of the registry.
/// </summary>
public sealed record StubState
{
    /// <summary>The current file format. Version 2 adds the server of each stub and the version groups.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The format of the flat layout, which reads as the current one with no server and no group.</summary>
    public const int FlatLayoutVersion = 1;

    /// <summary>Gets an empty registry.</summary>
    public static StubState Empty { get; } = new();

    /// <summary>Gets the file format.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Gets the film and episode stubs.</summary>
    public IReadOnlyList<StubEntry> Stubs { get; init; } = [];

    /// <summary>Gets the stub series.</summary>
    public IReadOnlyList<SeriesStub> Series { get; init; } = [];

    /// <summary>Gets the version groups, the source of truth for linking the versions of a title.</summary>
    public IReadOnlyList<VersionGroup> Groups { get; init; } = [];

    /// <summary>Gets the label of each server, fixed once given: only a rename changes it, and a removal frees it.</summary>
    public IReadOnlyDictionary<Guid, string> Labels { get; init; } = new Dictionary<Guid, string>();

    /// <summary>
    /// Gets the servers the administrator removed from the page. Only their stubs and catalogs are deleted: a server missing
    /// from the configuration for any other reason, such as a reset file, keeps them (decision 44).
    /// </summary>
    public IReadOnlyList<Guid> RemovedServers { get; init; } = [];

    /// <summary>Gets the stubs of renamed servers whose old files and items are not gone yet.</summary>
    public IReadOnlyList<StubRename> Renames { get; init; } = [];

    /// <summary>
    /// Gets these stubs, each followed by itself at every old path a pending server rename still keeps with an item, so that
    /// both items leave their version groups before a removal (Q351). An old path that a stub has now is left out.
    /// </summary>
    /// <param name="stubs">The stubs about to be removed.</param>
    /// <returns>The stubs, with their old paths.</returns>
    public IReadOnlyList<StubEntry> WithOldPaths(IEnumerable<StubEntry> stubs)
    {
        var current = Stubs.Select(stub => stub.RelativePath).ToHashSet(StringComparer.Ordinal);
        return stubs
            .SelectMany(stub => Renames
                .Where(rename => rename.RemovedItemId is null && string.Equals(rename.StubId, stub.Id, StringComparison.Ordinal) && !current.Contains(rename.OldPath))
                .Select(rename => stub with { RelativePath = rename.OldPath })
                .Prepend(stub))
            .ToList();
    }

    /// <summary>
    /// Gets the stored labels still taken: those of configured servers, and of servers left out of the settings without a
    /// removal that still have stubs. A removed server frees its label (decision 408).
    /// </summary>
    /// <param name="configured">The servers in the settings.</param>
    /// <returns>The labels still taken, by server.</returns>
    public Dictionary<Guid, string> TakenLabels(IEnumerable<Guid> configured)
    {
        var kept = configured.ToHashSet();
        var removed = RemovedServers.ToHashSet();
        var withStubs = Stubs.Select(stub => stub.ServerId).Concat(Series.Select(series => series.ServerId)).OfType<Guid>().ToHashSet();
        return Labels
            .Where(entry => kept.Contains(entry.Key) || (!removed.Contains(entry.Key) && withStubs.Contains(entry.Key)))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
    }
}
