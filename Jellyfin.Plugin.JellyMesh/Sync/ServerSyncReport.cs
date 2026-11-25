using System;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// The sync state of a server, as shown on the configuration page, to administrators only.
/// </summary>
/// <param name="ServerId">The server.</param>
/// <param name="ServerName">The display name.</param>
/// <param name="State">How far its entries can be trusted, null if it never synced.</param>
/// <param name="Failure">Why the last sync failed, if it did.</param>
/// <param name="LastAttemptUtc">When a sync last ended.</param>
/// <param name="LastSuccessUtc">When a sync last read the whole catalog.</param>
/// <param name="IsOffline">Whether the last sync could not reach the server. Its entries stay in the index.</param>
/// <param name="Movies">The films in the index.</param>
/// <param name="Series">The series in the index.</param>
/// <param name="Episodes">The episodes in the index.</param>
public sealed record ServerSyncReport(
    Guid ServerId,
    string ServerName,
    ServerSyncState? State,
    RemoteFailure? Failure,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastSuccessUtc,
    bool IsOffline,
    int Movies,
    int Series,
    int Episodes)
{
    /// <summary>
    /// Gets the whole story of the failure, null when there is none or it was before a restart.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>Gets the music tracks in the index.</summary>
    public int Tracks { get; init; }

    /// <summary>Gets the music videos in the index.</summary>
    public int MusicVideos { get; init; }

    /// <summary>Gets the home videos in the index.</summary>
    public int HomeVideos { get; init; }

    /// <summary>Gets the ebooks in the index.</summary>
    public int Books { get; init; }

    /// <summary>Gets the audiobook files in the index, where a book in several files has one entry per file.</summary>
    public int AudiobookFiles { get; init; }
}
