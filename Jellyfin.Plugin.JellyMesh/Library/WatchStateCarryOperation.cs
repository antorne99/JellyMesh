namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The operation a watch state carry comes from, which its record keeps (row 575).
/// </summary>
public enum WatchStateCarryOperation
{
    /// <summary>A manual merge of two titles of one library (decision 172).</summary>
    Merge,

    /// <summary>A pure rename of a stub, as a server rename makes it (row 572).</summary>
    Rename,

    /// <summary>The migration of the old stubs to the server folders (F15, F16).</summary>
    Migration
}
