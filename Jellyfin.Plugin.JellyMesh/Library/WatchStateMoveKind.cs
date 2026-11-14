namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What the old item of a watch state move is.
/// </summary>
public enum WatchStateMoveKind
{
    /// <summary>A film or an episode, whose state goes to the versions of its title.</summary>
    Title,

    /// <summary>A series, whose favorite and rating go to each new series stub (decision 150).</summary>
    Series
}
