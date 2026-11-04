using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// A remote library as offered for import.
/// </summary>
/// <param name="Id">The remote identifier.</param>
/// <param name="Name">The remote name. Untrusted text.</param>
/// <param name="CollectionType">The collection type.</param>
/// <param name="Selected">Whether it is set for import.</param>
public sealed record LibraryOption(string Id, string Name, string CollectionType, bool Selected);
