using System;
using Jellyfin.Plugin.JellyMesh.Storage;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The state of the plugin database as the page shows it (rows 427, 466, 469).
/// </summary>
/// <param name="State">The state.</param>
/// <param name="Reason">Why the database is not healthy; <c>null</c> when it is.</param>
/// <param name="Recovery">The last restore of a damaged database, until the admin dismisses it; <c>null</c> when there is none.</param>
/// <param name="MigratedKeptUntil">Until when the files of the older version are kept; <c>null</c> when there are none.</param>
public sealed record StoreStatus(StoreState State, string? Reason, StoreRecovery? Recovery, DateTimeOffset? MigratedKeptUntil);
