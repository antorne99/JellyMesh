using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The folder of a stub series. It lives as long as it has episode stubs.
/// </summary>
public sealed record SeriesStub
{
    /// <summary>Gets the stable ID.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the server whose folder holds the series, null in the flat layout.</summary>
    public Guid? ServerId { get; init; }

    /// <summary>Gets the folder relative to the federated folder. Fixed at creation, as for <see cref="StubEntry"/>.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Gets the last work key seen. Only a hint to find the stub again: a key can change.</summary>
    public required string WorkKey { get; init; }

    /// <summary>Gets the series items known on the remotes.</summary>
    public IReadOnlyList<StubSource> Sources { get; init; } = [];

    /// <summary>Gets a hash of what was written for the series, null until it is on disk.</summary>
    public string? ContentHash { get; init; }

    /// <summary>Gets a tag of the images written, null until they are on disk.</summary>
    public string? ImageTag { get; init; }

    /// <summary>Gets a value indicating whether the item still needs its first refresh (Phase 0, V17).</summary>
    public bool NeedsRefresh { get; init; }

    /// <summary>Gets the season folders written for the series, as of the last run that had work for it.</summary>
    public IReadOnlyList<SeasonStub> Seasons { get; init; } = [];
}
