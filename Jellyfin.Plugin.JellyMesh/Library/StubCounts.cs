namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What a run did to the stubs of one server's folder.
/// </summary>
/// <param name="Created">How many stubs were created.</param>
/// <param name="Updated">How many stubs were updated.</param>
/// <param name="Removed">How many stubs were removed.</param>
/// <param name="Failed">How many stubs or series could not be written or removed.</param>
public sealed record StubCounts(int Created, int Updated, int Removed, int Failed);
