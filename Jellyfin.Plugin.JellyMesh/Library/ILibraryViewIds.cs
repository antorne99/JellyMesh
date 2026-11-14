using System;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The ids of the views Jellyfin builds from a library, which the home order and exclusions name. They exist only from
/// Jellyfin 12.2, while the plugin builds against 12.1 (Q34, Q38).
/// </summary>
public interface ILibraryViewIds
{
    /// <summary>Gets a value indicating whether the running server has them.</summary>
    bool IsAvailable { get; }

    /// <summary>The id of the shadow view of a library.</summary>
    /// <param name="name">The library name.</param>
    /// <param name="parentId">The library id.</param>
    /// <param name="viewType">The library type.</param>
    /// <returns>The view id.</returns>
    Guid ShadowViewId(string name, Guid parentId, CollectionType? viewType);

    /// <summary>The id of the named view of a library for a user.</summary>
    /// <param name="user">The user.</param>
    /// <param name="parentId">The library id.</param>
    /// <param name="viewType">The library type.</param>
    /// <returns>The view id.</returns>
    Guid NamedViewId(User user, Guid parentId, CollectionType? viewType);
}
