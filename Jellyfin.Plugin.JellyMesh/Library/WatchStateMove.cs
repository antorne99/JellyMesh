using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// An old item whose watch state goes to the new versions of its title, as the migration or a server rename pairs them.
/// </summary>
public sealed record WatchStateMove
{
    /// <summary>Gets the Jellyfin item of the old stub.</summary>
    public required Guid FromItemId { get; init; }

    /// <summary>Gets the old stub, under which the last server that served each user is recorded (D15).</summary>
    public required string FromStubId { get; init; }

    /// <summary>Gets the servers of the old stub, best first (§4.3).</summary>
    public required IReadOnlyList<Guid> Servers { get; init; }

    /// <summary>Gets the new versions of the title, the local copy included if there is one.</summary>
    public required IReadOnlyList<WatchStateTarget> Versions { get; init; }

    /// <summary>
    /// Makes the move of an old stub, whose sources give its servers best first.
    /// </summary>
    /// <param name="from">The old stub.</param>
    /// <param name="fromItemId">The Jellyfin item of the old stub.</param>
    /// <param name="versions">The new versions of the title.</param>
    /// <returns>The move.</returns>
    public static WatchStateMove Of(StubEntry from, Guid fromItemId, IReadOnlyList<WatchStateTarget> versions)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(versions);

        return new WatchStateMove
        {
            FromItemId = fromItemId,
            FromStubId = from.Id,
            Servers = from.Sources.Select(source => source.ServerId).Distinct().ToList(),
            Versions = versions
        };
    }
}
