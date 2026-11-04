namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// A person of a remote catalog.
/// </summary>
/// <param name="Name">The name.</param>
/// <param name="Role">The role.</param>
/// <param name="Type">The kind, such as <c>Actor</c> or <c>Director</c>.</param>
public sealed record RemotePerson(string? Name, string? Role, string? Type);
