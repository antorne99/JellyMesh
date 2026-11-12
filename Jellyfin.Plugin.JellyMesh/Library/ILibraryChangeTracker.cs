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
}
