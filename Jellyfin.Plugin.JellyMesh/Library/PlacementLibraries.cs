using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The libraries of the server read once their registry is followed, with those the plugin made for a server.
/// </summary>
/// <param name="Folders">The libraries of the server.</param>
/// <param name="Automatic">Those of <paramref name="Folders"/> the plugin made for a server, kept or still to delete.</param>
public sealed record PlacementLibraries(IReadOnlyList<VirtualFolderInfo> Folders, IReadOnlySet<VirtualFolderInfo> Automatic);
