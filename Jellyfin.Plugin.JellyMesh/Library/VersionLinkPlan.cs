using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What a run changes to link the versions of one title.
/// </summary>
/// <param name="Updates">The items to save, in order: items detached from the group, the alternates, then the primary.</param>
/// <param name="Joined">The members that were not linked to the primary before.</param>
public sealed record VersionLinkPlan(IReadOnlyList<VersionLinkUpdate> Updates, IReadOnlyList<Guid> Joined);
