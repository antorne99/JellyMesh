using System;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A library whose complete read came back empty while stubs depend on it. Removing them could wipe a library by mistake,
/// so they stay until the library has items again, or an administrator deselects it or removes the server.
/// </summary>
/// <param name="ServerId">The server.</param>
/// <param name="ServerName">The display name of the server.</param>
/// <param name="LibraryId">The remote library.</param>
/// <param name="Stubs">How many stubs are held.</param>
public sealed record HeldLibrary(Guid ServerId, string ServerName, string LibraryId, int Stubs);
