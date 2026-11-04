namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// Works out the decisions from the stored entries. They are never stored, because a new source can change identities.
/// </summary>
public interface IFederationResolver
{
    /// <summary>
    /// Resolves the whole catalog against the local library.
    /// </summary>
    /// <returns>The sources of each identity.</returns>
    FederationView Resolve();
}
