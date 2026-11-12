using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// How Jellyfin links an item to other versions.
/// </summary>
/// <param name="ItemId">The item.</param>
/// <param name="PrimaryVersionId">The item it is an alternate of, null if none.</param>
/// <param name="LinkedIds">The items linked to it as alternates.</param>
public sealed record VersionLinkState(Guid ItemId, Guid? PrimaryVersionId, IReadOnlyList<Guid> LinkedIds);
