using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The stubs written in the federated folder, by their stable ID. Reads need no lock, and they survive a stale or lost
/// index, because the sources of a stub are stored with it.
/// </summary>
public interface IStubRegistry
{
    /// <summary>
    /// Finds a stub by ID, the last segment of the URL in its <c>.strm</c>.
    /// </summary>
    /// <param name="stubId">The stub.</param>
    /// <returns>The stub, null if there is none.</returns>
    StubEntry? Find(string stubId);

    /// <summary>
    /// Finds a stub by the path of its <c>.strm</c>.
    /// </summary>
    /// <param name="path">The file path, <c>BaseItem.Path</c>.</param>
    /// <returns>The stub, null for anything that is not one.</returns>
    StubEntry? FindByPath(string? path);

    /// <summary>
    /// Finds the version group of a stub, read from memory.
    /// </summary>
    /// <param name="stubId">The stub.</param>
    /// <returns>The group, null if the stub is in none.</returns>
    VersionGroup? FindGroup(string stubId);

    /// <summary>
    /// Finds the version group of a local copy, read from memory.
    /// </summary>
    /// <param name="itemId">The Jellyfin item of the local copy.</param>
    /// <returns>The group, null if the item is in none.</returns>
    VersionGroup? FindGroupOfLocal(Guid itemId);

    /// <summary>
    /// Finds the track stubs with a dedup key: the copies of one track, read from memory (row 457, D65).
    /// </summary>
    /// <param name="identityKey">The dedup key of the track.</param>
    /// <returns>The track stubs, empty for a key no track has.</returns>
    IReadOnlyList<StubEntry> FindCopies(string identityKey);

    /// <summary>
    /// Tells whether a stub or a stub series of a server may still be in the registry. True while the registry cannot tell:
    /// not read, read before the database was opened again, or emptied by rows that did not read until a full materialization.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>Whether the server may still have titles in the federated folder.</returns>
    bool HasTitlesOf(Guid serverId);

    /// <summary>Gives the relative type folders of a server that hold its titles of one type, in the remote library layout.</summary>
    /// <param name="serverId">The server.</param>
    /// <param name="type">The type folder.</param>
    /// <returns>The folders, or null while the registry cannot tell, as for <see cref="HasTitlesOf"/>.</returns>
    IReadOnlyList<string>? TypeFoldersOf(Guid serverId, TypeFolder type);

    /// <summary>
    /// Tells whether any title is still in the flat or the 0.5 server folder layout, or whether the registry is empty while the
    /// federated folder holds stub files of those layouts, as after a damage (§10 row 444).
    /// </summary>
    /// <returns>The answer, or null while the registry cannot tell, as for <see cref="HasTitlesOf"/>.</returns>
    bool? UsesOlderLayout();
}
