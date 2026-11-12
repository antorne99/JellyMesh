using System;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.JellyMesh.Segments;

/// <summary>
/// Decides whether a playback offers the segments of its item: they fit only the source they came from, or a source with
/// the same duration (D53).
/// </summary>
public sealed class SegmentOffer
{
    private readonly IMediaSegmentManager _manager;
    private readonly StubSegments _segments;
    private readonly StubLookup _stubs;
    private readonly PlaybackSessionStore _sessions;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentOffer"/> class.
    /// </summary>
    /// <param name="manager">The segment manager.</param>
    /// <param name="segments">The segments of the stubs.</param>
    /// <param name="stubs">The stub lookup.</param>
    /// <param name="sessions">The playback sessions.</param>
    public SegmentOffer(IMediaSegmentManager manager, StubSegments segments, StubLookup stubs, PlaybackSessionStore sessions)
    {
        _manager = manager;
        _segments = segments;
        _stubs = stubs;
        _sessions = sessions;
    }

    /// <summary>
    /// Tells whether a playback of an item from a source offers its segments.
    /// </summary>
    /// <param name="itemId">The item that serves.</param>
    /// <param name="served">The remote source that serves, null when none.</param>
    /// <returns>True when the client may use the segments.</returns>
    public bool Offers(Guid itemId, StubSource? served)
    {
        if (!_manager.HasSegments(itemId))
        {
            return false;
        }

        // Since a restart the origin is unknown: the segments came from the best source.
        var origin = _segments.OriginOf(itemId)
            ?? (_stubs.Find(itemId) is { Sources.Count: > 0 } stub ? new SegmentOrigin(stub.Sources[0], _segments.VersionOf(stub.Sources[0])?.RunTimeTicks) : null);
        if (origin is null)
        {
            return true;
        }

        if (served is null)
        {
            return false;
        }

        if (served.ServerId == origin.Source.ServerId
            && string.Equals(served.ItemId, origin.Source.ItemId, StringComparison.Ordinal)
            && string.Equals(served.MediaSourceId, origin.Source.MediaSourceId, StringComparison.Ordinal))
        {
            return true;
        }

        return origin.RunTimeTicks is long from && from > 0
            && _segments.VersionOf(served)?.RunTimeTicks is long to && to > 0
            && MetadataComparison.RuntimesAgree(from, to);
    }

    /// <summary>
    /// Sets whether each source of a remote playback offers segments, by the version served and its session's source.
    /// </summary>
    /// <param name="response">The answer built from the remote.</param>
    /// <param name="served">The version that served, null to take it from the session.</param>
    public void OfferOnRemote(PlaybackInfoResponse response, PlaybackIdentity? served)
    {
        ArgumentNullException.ThrowIfNull(response);

        var session = _sessions.Find(response.PlaySessionId);
        var offered = session is not null && Offers((served ?? session.Identity).ItemId, session.Source);
        foreach (var source in response.MediaSources)
        {
            source.HasSegments = offered;
        }
    }
}
