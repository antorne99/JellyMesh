using System;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// A library visible to the dedicated user.
/// </summary>
/// <param name="Id">The remote library identifier.</param>
/// <param name="Name">The name on the remote. Untrusted text.</param>
/// <param name="CollectionType">The remote collection type.</param>
public sealed record RemoteLibrary(string Id, string Name, string CollectionType);
