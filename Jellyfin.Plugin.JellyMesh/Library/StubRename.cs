using System;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A stub moved to the label of its renamed server, whose old file and item stay until its watch state is carried (Q38).
/// </summary>
/// <param name="StubId">The stub, which keeps its ID.</param>
/// <param name="OldPath">The old path of the <c>.strm</c>, relative to the federated folder.</param>
public sealed record StubRename(string StubId, string OldPath)
{
    /// <summary>Gets the old item once its files are deleted, until the library no longer has it.</summary>
    public Guid? RemovedItemId { get; init; }

    /// <summary>Gets a value indicating whether the carry records of the removed item may be forgotten once it is gone.</summary>
    public bool Forgets { get; init; }
}
