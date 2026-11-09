using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// An immutable copy of the registry.
/// </summary>
public sealed record StubState
{
    /// <summary>The current file format.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Gets an empty registry.</summary>
    public static StubState Empty { get; } = new();

    /// <summary>Gets the file format.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Gets the film and episode stubs.</summary>
    public IReadOnlyList<StubEntry> Stubs { get; init; } = [];

    /// <summary>Gets the stub series.</summary>
    public IReadOnlyList<SeriesStub> Series { get; init; } = [];

    /// <summary>
    /// Gets the servers the administrator removed from the page. Only their stubs and catalogs are deleted: a server missing
    /// from the configuration for any other reason, such as a reset file, keeps them (decision 44).
    /// </summary>
    public IReadOnlyList<Guid> RemovedServers { get; init; } = [];
}
