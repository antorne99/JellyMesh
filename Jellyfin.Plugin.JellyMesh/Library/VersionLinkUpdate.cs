using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The links an item must have after a run.
/// </summary>
/// <param name="ItemId">The item.</param>
/// <param name="PrimaryVersionId">The item it is an alternate of, null if none.</param>
/// <param name="LinkedIds">The items linked to it as alternates, in order.</param>
public sealed record VersionLinkUpdate(Guid ItemId, Guid? PrimaryVersionId, IReadOnlyList<Guid> LinkedIds);
