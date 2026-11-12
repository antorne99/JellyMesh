using System;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The primary version of a group, and the stub to record as its primary.
/// </summary>
/// <param name="ItemId">The item that holds the other versions.</param>
/// <param name="PrimaryStubId">The stub to record, null when a local copy is the primary.</param>
public sealed record VersionPrimary(Guid ItemId, string? PrimaryStubId);
