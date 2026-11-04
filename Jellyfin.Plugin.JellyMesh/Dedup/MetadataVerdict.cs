namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// What a metadata comparison concluded.
/// </summary>
internal enum MetadataVerdict
{
    /// <summary>Not enough to compare: the copies do not merge, but nothing says they are different.</summary>
    Inconclusive,

    /// <summary>The metadata confirm that the copies are the same film.</summary>
    Confirmed,

    /// <summary>A field differs, so the copies are treated as different films.</summary>
    Contradicted
}
