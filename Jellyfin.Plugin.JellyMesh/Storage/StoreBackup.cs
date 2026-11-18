using System;

namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// A nightly copy of the plugin database, next to it in the plugin data folder.
/// </summary>
/// <param name="FilePath">The path of the copy.</param>
/// <param name="CreatedAt">When the copy was made, from its name.</param>
public sealed record StoreBackup(string FilePath, DateTimeOffset CreatedAt);
