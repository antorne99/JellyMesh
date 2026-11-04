using System;
using Jellyfin.Plugin.JellyMesh.Configuration;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// The times of a playback that the administrator can change, as they are now.
/// </summary>
/// <param name="RemotePlaybackInfo">The limit on a <c>PlaybackInfo</c> call to a remote.</param>
/// <param name="StreamOpen">The limit on opening a stream.</param>
/// <param name="Idle">The time without requests after which a transcode is closed.</param>
/// <param name="SessionRetention">The time without requests after which a session is forgotten.</param>
/// <param name="PolicyRetention">How long the transcode policy of a remote is kept.</param>
public sealed record PlaybackTimes(TimeSpan RemotePlaybackInfo, TimeSpan StreamOpen, TimeSpan Idle, TimeSpan SessionRetention, TimeSpan PolicyRetention)
{
    /// <summary>
    /// Reads the times of a configuration.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The times.</returns>
    public static PlaybackTimes From(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new PlaybackTimes(
            TimeSpan.FromSeconds(configuration.RemotePlaybackInfoTimeoutSeconds),
            TimeSpan.FromSeconds(configuration.StreamOpenTimeoutSeconds),
            TimeSpan.FromSeconds(configuration.PlaybackIdleSeconds),
            TimeSpan.FromHours(configuration.SessionRetentionHours),
            TimeSpan.FromMinutes(configuration.PolicyCacheMinutes));
    }
}
