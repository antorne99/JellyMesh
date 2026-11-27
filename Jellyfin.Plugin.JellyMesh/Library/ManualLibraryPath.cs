namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A type folder the admin adds to a library by hand.
/// </summary>
/// <param name="Path">The folder.</param>
/// <param name="LibraryType">The local library type it needs, such as <c>movies</c>.</param>
/// <param name="RemoteLibrary">The name of its remote library.</param>
public sealed record ManualLibraryPath(string Path, string LibraryType, string RemoteLibrary);
