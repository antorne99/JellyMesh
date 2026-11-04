using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// How a playback goes on.
/// </summary>
/// <param name="Kind">The kind of outcome.</param>
/// <param name="Response">The answer for the client, when a remote serves the stream.</param>
public sealed record PlaybackOutcome(PlaybackOutcomeKind Kind, PlaybackInfoResponse? Response = null);
