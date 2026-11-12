using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Remote;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Activity;

/// <summary>
/// The entries JellyMesh writes to the activity log (decision 49). They name servers by their display name only.
/// </summary>
public static class ActivityEntries
{
    /// <summary>
    /// The type of the entry for a server that stopped answering.
    /// </summary>
    public const string ServerOfflineType = "JellyMeshServerOffline";

    /// <summary>
    /// The type of the entry for a server that answers again.
    /// </summary>
    public const string ServerReachableType = "JellyMeshServerReachable";

    /// <summary>
    /// The type of the entry for a server whose sync started failing.
    /// </summary>
    public const string SyncFailedType = "JellyMeshSyncFailed";

    /// <summary>
    /// The type of the entry for each completed sync.
    /// </summary>
    public const string SyncCompletedType = "JellyMeshSyncCompleted";

    /// <summary>
    /// The type of the entry for a server whose stored password no longer decrypts.
    /// </summary>
    public const string PasswordUnreadableType = "JellyMeshPasswordUnreadable";

    /// <summary>
    /// Builds the entry for a server that stopped answering.
    /// </summary>
    /// <param name="serverName">The display name of the server.</param>
    /// <param name="failure">Why it is considered offline.</param>
    /// <returns>The entry.</returns>
    public static ActivityEntry ServerOffline(string serverName, RemoteFailure failure)
    {
        return new ActivityEntry($"JellyMesh: remote server {serverName} is offline", ServerOfflineType, LogLevel.Warning)
        {
            ShortOverview = failure.ToString(),
            Overview = "Its titles stay visible. They play from a backup source if there is one, otherwise playback reports an error."
        };
    }

    /// <summary>
    /// Builds the entry for a server that answers again.
    /// </summary>
    /// <param name="serverName">The display name of the server.</param>
    /// <returns>The entry.</returns>
    public static ActivityEntry ServerReachable(string serverName)
    {
        return new ActivityEntry($"JellyMesh: remote server {serverName} is reachable again", ServerReachableType, LogLevel.Information);
    }

    /// <summary>
    /// Builds the entry for a server whose sync started failing.
    /// </summary>
    /// <param name="serverName">The display name of the server.</param>
    /// <param name="failure">Why the sync failed.</param>
    /// <returns>The entry.</returns>
    public static ActivityEntry SyncFailed(string serverName, RemoteFailure failure)
    {
        return new ActivityEntry($"JellyMesh: sync of remote server {serverName} failed", SyncFailedType, LogLevel.Warning)
        {
            ShortOverview = failure.ToString(),
            Overview = "Its titles are kept as they were until a sync succeeds."
        };
    }

    /// <summary>
    /// Builds the entry for a completed sync.
    /// </summary>
    /// <param name="servers">How many servers the sync went through.</param>
    /// <param name="failed">The servers that could not be read, by display name, with why.</param>
    /// <returns>The entry.</returns>
    public static ActivityEntry SyncCompleted(int servers, IReadOnlyCollection<KeyValuePair<string, RemoteFailure>> failed)
    {
        var read = servers - failed.Count;
        return new ActivityEntry("JellyMesh: catalog sync completed", SyncCompletedType, failed.Count == 0 ? LogLevel.Information : LogLevel.Warning)
        {
            ShortOverview = string.Format(CultureInfo.InvariantCulture, "{0} of {1} servers read", read, servers),
            Overview = failed.Count == 0
                ? null
                : "Not read: " + string.Join(", ", failed.Select(server => $"{server.Key} ({server.Value})"))
        };
    }

    /// <summary>
    /// Builds the entry for a server whose stored password no longer decrypts.
    /// </summary>
    /// <param name="serverName">The display name of the server.</param>
    /// <returns>The entry.</returns>
    public static ActivityEntry PasswordUnreadable(string serverName)
    {
        return new ActivityEntry($"JellyMesh: the stored password of remote server {serverName} no longer decrypts", PasswordUnreadableType, LogLevel.Error)
        {
            Overview = "Sync and remote playback are suspended until the password is entered again on the JellyMesh configuration page."
        };
    }
}
