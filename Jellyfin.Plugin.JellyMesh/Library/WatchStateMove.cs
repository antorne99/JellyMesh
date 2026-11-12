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
    /// <returns>The move.</returns>
    public static WatchStateMove Of(StubEntry from, Guid fromItemId, IReadOnlyList<WatchStateTarget> targets)
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
            Targets = targets
        };
    }

    /// <summary>
    /// Makes the move of an old series stub, whose favorite and rating go to each new series stub (decision 150).
    /// </summary>
    /// <param name="from">The old series stub.</param>
    /// <param name="fromItemId">The Jellyfin item of the old series.</param>
    /// <param name="targets">The new series stubs.</param>
    /// <returns>The move.</returns>
    public static WatchStateMove OfSeries(SeriesStub from, Guid fromItemId, IReadOnlyList<WatchStateTarget> targets)
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
            Targets = targets
        };
    }
}
