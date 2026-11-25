namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A stub, or a series, that split from another of its server which now is in another local library (§10 row 507): it takes
/// the whole watch state of that one, once, when a scan has made its item.
/// </summary>
/// <param name="StubId">The new stub or series.</param>
/// <param name="FromStubId">The stub or series it split from, which stays until the state is handed over.</param>
public sealed record StubSplit(string StubId, string FromStubId);
