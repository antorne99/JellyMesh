using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// An item a JellyMesh remote says has changed.
/// </summary>
/// <param name="Id">The item on the remote.</param>
/// <param name="Type">The item type.</param>
/// <param name="LibraryIds">The shared libraries it is in.</param>
public sealed record RemoteChange(string Id, string Type, IReadOnlyList<string> LibraryIds);
