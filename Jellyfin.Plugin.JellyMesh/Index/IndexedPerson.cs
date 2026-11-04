namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// A person of a catalog entry.
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="Role">The role.</param>
/// <param name="Type">The kind, such as <c>Actor</c>.</param>
public sealed record IndexedPerson(string? Name, string? Role, string? Type);
