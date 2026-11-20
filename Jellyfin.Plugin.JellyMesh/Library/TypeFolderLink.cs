namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The link of one folder of a server to the libraries.
/// </summary>
/// <param name="Path">The folder.</param>
/// <param name="LibraryType">The local library type it needs, such as <c>movies</c>.</param>
/// <param name="RemoteLibrary">The name of its remote library, empty for a 0.5 folder.</param>
/// <param name="Link">Its link.</param>
public sealed record TypeFolderLink(string Path, string LibraryType, string RemoteLibrary, FederatedPathStatus Link);
