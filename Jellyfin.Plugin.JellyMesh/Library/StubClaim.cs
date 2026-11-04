using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Index;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The stubs of a content.
/// </summary>
/// <param name="Content">The content.</param>
/// <param name="Stub">Its stub, null if it has none.</param>
public sealed record StubClaim(ResolvedContent Content, StubEntry? Stub)
{
    /// <summary>
    /// Gets the other stubs of the content: copies shown apart until now, that became one title. They wait for the post-scan
    /// task, which hands their watch state over before removing them (decision 2).
    /// </summary>
    public IReadOnlyList<StubEntry> Merged { get; init; } = [];
}
