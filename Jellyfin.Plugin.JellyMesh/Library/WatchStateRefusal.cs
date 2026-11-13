namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Why the watch state carry refused a move (Q216, Q259).
/// </summary>
public enum WatchStateRefusal
{
    /// <summary>No federated folder is set.</summary>
    NoFederatedFolder,

    /// <summary>The old item is not the stub of the move: another kind, or not at its path in the federated folder.</summary>
    OldItemNotStub,

    /// <summary>The move has no new item but the old one.</summary>
    NoTarget,

    /// <summary>A new item is not a stub, or a series stub, of the server given for it.</summary>
    TargetNotStub,

    /// <summary>The move gives more than one local copy.</summary>
    SeveralLocalVersions,

    /// <summary>A local copy is in no version group, or lies in the federated folder.</summary>
    LocalNotInGroup,

    /// <summary>A local copy is linked as a version of another item instead of heading its group.</summary>
    LocalNotPrimary
}
