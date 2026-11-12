using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// An old item whose watch state goes to the new items of its title, as the migration or a server rename pairs them.
/// Made only from the old stub, so that its ID and path always name the same stub.
/// </summary>
public sealed record WatchStateMove
{
    private WatchStateMove()
    {
    }

    /// <summary>Gets what the old item is: a film or an episode, or a series.</summary>
    public WatchStateMoveKind Kind { get; private init; }

    /// <summary>Gets the Jellyfin item of the old stub.</summary>
    public Guid FromItemId { get; private init; }

    /// <summary>Gets the old stub, under which the last server that served each user is recorded (D15).</summary>
    public string FromStubId { get; private init; } = string.Empty;

    /// <summary>Gets the path of the old stub relative to the federated folder, which its item must have.</summary>
    public string FromPath { get; private init; } = string.Empty;

    /// <summary>Gets the servers of the old stub, best first (§4.3).</summary>
    public IReadOnlyList<Guid> Servers { get; private init; } = [];

    /// <summary>
    /// Gets a value indicating whether the resume position is carried. A merge leaves it on each version (decision 172).
    /// </summary>
    public bool CarriesPosition { get; private init; } = true;

    /// <summary>
    /// Gets a value indicating whether the old item is removed right after the carry, as a server rename (F14) and the
    /// automatic migration (F16) do. Once carried for every user, its carry records go (decision 179).
    /// </summary>
    public bool RemovesOldItem { get; private init; }

    /// <summary>
    /// Gets the new items: for a film or an episode the new versions, the local copy included if there is one, and the
    /// new stubs never linked with them (D3); for a series its new series stubs.
    /// </summary>
    public IReadOnlyList<WatchStateTarget> Targets { get; private init; } = [];

    /// <summary>
    /// Makes the move of an old film or episode stub, whose sources give its servers best first.
    /// </summary>
    /// <param name="from">The old stub.</param>
    /// <param name="fromItemId">The Jellyfin item of the old stub.</param>
    /// <param name="targets">The new versions of the title, and the stubs of different ranges (D3).</param>
    /// <param name="removesOldItem">
    /// Whether the old stub is removed right after: true for a server rename (F14) and the automatic migration (F16),
    /// false for the manual migration, which leaves the old stubs playable (F15, D12). See decision 179.
    /// </param>
    /// <returns>The move.</returns>
    public static WatchStateMove Of(StubEntry from, Guid fromItemId, IReadOnlyList<WatchStateTarget> targets, bool removesOldItem = false)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(targets);

        return new WatchStateMove
        {
            Kind = WatchStateMoveKind.Title,
            FromItemId = fromItemId,
            FromStubId = from.Id,
            FromPath = from.RelativePath,
            Servers = from.Sources.Select(source => source.ServerId).Distinct().ToList(),
            RemovesOldItem = removesOldItem,
            Targets = targets
        };
    }

    /// <summary>
    /// Makes the move of a film stub whose title a manual merge joined with another of the same library: favorite, rating
    /// and play count go to the primary, played to every version, and the position stays (decision 172). The stub may stay
    /// as a version, so the move never says it is removed and its carry records stay (decisions 155, 179).
    /// </summary>
    /// <param name="from">The stub of the title that joined, which may stay as a version.</param>
    /// <param name="fromItemId">The Jellyfin item of that stub.</param>
    /// <param name="targets">The versions of the merged title; the stub itself is left out of them.</param>
    /// <returns>The move.</returns>
    public static WatchStateMove OfMerge(StubEntry from, Guid fromItemId, IReadOnlyList<WatchStateTarget> targets)
        => Of(from, fromItemId, targets) with { CarriesPosition = false };

    /// <summary>
    /// Makes the move of an old series stub, whose favorite and rating go to each new series stub (decision 150).
    /// </summary>
    /// <param name="from">The old series stub.</param>
    /// <param name="fromItemId">The Jellyfin item of the old series.</param>
    /// <param name="targets">The new series stubs.</param>
    /// <param name="removesOldItem">
    /// Whether the old series stub is removed right after: true for a server rename (F14) and the automatic migration
    /// (F16), false for the manual migration (F15, D12). See decision 179.
    /// </param>
    /// <returns>The move.</returns>
    public static WatchStateMove OfSeries(SeriesStub from, Guid fromItemId, IReadOnlyList<WatchStateTarget> targets, bool removesOldItem = false)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(targets);

        return new WatchStateMove
        {
            Kind = WatchStateMoveKind.Series,
            FromItemId = fromItemId,
            FromStubId = from.Id,
            FromPath = from.RelativePath,
            Servers = from.Sources.Select(source => source.ServerId).Distinct().ToList(),
            RemovesOldItem = removesOldItem,
            Targets = targets
        };
    }
}
