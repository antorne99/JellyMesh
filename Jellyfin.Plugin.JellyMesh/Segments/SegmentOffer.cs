using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.JellyMesh.Segments;

/// <summary>
/// Decides whether a playback offers the segments of its item: they fit only the source they came from, or a source with
/// the same duration (D53). Remembers what each device of a user plays, so that a direct read of the segments gets none
/// while a copy they do not fit plays there (Q255).
/// </summary>
public sealed class SegmentOffer
{
    private readonly IMediaSegmentManager _manager;
    private readonly StubSegments _segments;
    private readonly StubLookup _stubs;
    private readonly PlaybackSessionStore _sessions;
    private readonly ConcurrentDictionary<(Guid UserId, string DeviceId), Played> _played = new();

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
    /// <param name="routeItemId">The item the client asked for, which it may read the segments of.</param>
    public void OfferOnRemote(PlaybackInfoResponse response, PlaybackIdentity? served, Guid? routeItemId = null)
    {
        ArgumentNullException.ThrowIfNull(response);

        var session = _sessions.Find(response.PlaySessionId);
        var identity = served ?? session?.Identity;
        var offered = session is not null && Offers(identity!.ItemId, session.Source);
        foreach (var source in response.MediaSources)
        {
            source.HasSegments = offered;
        }

        if (session is not null)
        {
            Record(identity!, session.Source, session.Id, false, session.Identity.ItemId, routeItemId);
        }
    }

    /// <summary>
    /// Sets whether each source of a local transcode of a stub offers segments, by the remote file it reads.
    /// </summary>
    /// <param name="response">The answer of stock.</param>
    /// <param name="served">The version served.</param>
    /// <param name="file">The remote source of the file, null when none.</param>
    /// <param name="routeItemId">The item the client asked for, which it may read the segments of.</param>
    public void OfferOnLocal(PlaybackInfoResponse response, PlaybackIdentity served, StubSource? file, Guid routeItemId)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(served);

        var offered = Offers(served.ItemId, file);
        foreach (var source in response.MediaSources)
        {
            source.HasSegments &= offered;
        }

        if (!string.IsNullOrEmpty(response.PlaySessionId))
        {
            Record(served, file, response.PlaySessionId, true, routeItemId);
        }
    }

    /// <summary>
    /// Tells whether a direct read of the segments of an item may answer them, by what the device of the user plays.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device, null when the request names none.</param>
    /// <param name="itemId">The item asked for.</param>
    /// <returns>False only while the device plays a copy of that item the segments do not fit.</returns>
    public bool ServesDirect(Guid userId, string? deviceId, Guid itemId)
    {
        var key = (userId, (deviceId ?? string.Empty).ToUpperInvariant());
        if (!_played.TryGetValue(key, out var played) || !played.Items.Contains(itemId))
        {
            return true;
        }

        if (!IsLive(played))
        {
            _played.TryRemove(KeyValuePair.Create(key, played));
            return true;
        }

        return Offers(itemId, played.Source);
    }

    // Records what a device plays, in place of what it played before, and forgets the plays that ended.
    private void Record(PlaybackIdentity identity, StubSource? source, string playSessionId, bool local, params Guid?[] items)
    {
        foreach (var (key, ended) in _played.Where(entry => !IsLive(entry.Value)).ToList())
        {
            _played.TryRemove(KeyValuePair.Create(key, ended));
        }

        var named = items.OfType<Guid>().Append(identity.ItemId).ToHashSet();
        _played[(identity.UserId, identity.DeviceId.ToUpperInvariant())] = new Played(named, source, playSessionId, local);
    }

    private bool IsLive(Played played)
    {
        return played.Local ? _sessions.LocalItemOf(played.PlaySessionId) is not null : _sessions.Find(played.PlaySessionId) is not null;
    }

    private sealed record Played(IReadOnlySet<Guid> Items, StubSource? Source, string PlaySessionId, bool Local);
}
