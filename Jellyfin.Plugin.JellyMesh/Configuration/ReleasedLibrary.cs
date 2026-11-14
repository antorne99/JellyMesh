using System;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// A library of a removed server that the plugin left to the admin, named by that server and its name (decision 158).
/// </summary>
/// <param name="ServerId">The local identifier the removed server had.</param>
/// <param name="Name">The name of the library when it was left.</param>
public sealed record ReleasedLibrary(Guid ServerId, [Required] string Name);
