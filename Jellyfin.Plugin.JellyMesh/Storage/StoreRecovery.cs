using System;

namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// A damaged database set aside and replaced by the latest nightly copy, or by an empty one, which the page shows until the
/// admin dismisses it (row 427, D16).
/// </summary>
/// <param name="RecoveredAt">When the damaged file was set aside.</param>
/// <param name="DamagedFile">The name of the file set aside, in the plugin data folder.</param>
/// <param name="CopyDate">When the copy the database restarted from was made; <c>null</c> when it restarted empty.</param>
public sealed record StoreRecovery(DateTimeOffset RecoveredAt, string DamagedFile, DateTimeOffset? CopyDate);
