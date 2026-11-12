using System;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Remembers the libraries on the federated folder at each sync, to tell the page what changed since (Q53).
/// </summary>
public interface ILibraryChangeTracker
{
    /// <summary>Records the libraries on the federated folder, before the sync links the versions again.</summary>
    void Record();

    /// <summary>Compares the libraries on the federated folder with the last record.</summary>
    /// <returns>The changes, or <see cref="LibraryChanges.None"/>.</returns>
    LibraryChanges Compare();

    /// <summary>
    /// Carries a library the plugin renamed to its new id in the last record, so the rename is no change, also while the
    /// library has no id until its scan.
    /// </summary>
    /// <param name="oldId">The id before the rename.</param>
    /// <param name="newId">The id after the rename.</param>
    /// <param name="newName">The new name.</param>
    void Renamed(Guid oldId, Guid newId, string newName);
}
