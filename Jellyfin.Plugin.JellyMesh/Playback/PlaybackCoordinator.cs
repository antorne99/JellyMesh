using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Walks the chain of §3.1: the sources in order of quality, then the local transcode. Whatever it answers with has
/// already been tried: a remote gives no signal that it cannot serve, so the only proof is the first segment.
/// </summary>
public sealed class PlaybackCoordinator
{
    private const string ProbeRange = "bytes=0-0";

    private readonly IRemoteServerRegistry _registry;
    private readonly IRemotePlaybackClient _client;
    private readonly PlaybackSessionStore _sessions;
    private readonly IPluginConfigurationStore _store;
    private readonly ILastServedSourceStore _lastServed;
    private readonly TimeProvider _clock;
    private readonly ILogger<PlaybackCoordinator> _logger;
    private readonly ConcurrentDictionary<(Guid UserId, string StubId), WorkingSource> _workingSources = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _filesAtLimit = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, CachedPolicy> _policies = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _callGates = new();
    private readonly ConcurrencyLimit<Guid> _streams = new(PlaybackLimits.StreamsPerServer);
    private readonly SourceFailures _failures = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackCoordinator"/> class.
    /// </summary>
    /// <param name="registry">The server registry.</param>
    /// <param name="client">The playback client.</param>
    /// <param name="sessions">The sessions.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="lastServed">The last server that served each stub to each user.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackCoordinator(
        IRemoteServerRegistry registry,
        IRemotePlaybackClient client,
        PlaybackSessionStore sessions,
        IPluginConfigurationStore store,
        ILastServedSourceStore lastServed,
        TimeProvider clock,
        ILogger<PlaybackCoordinator> logger)
    {
        _registry = registry;
        _client = client;
        _sessions = sessions;
        _store = store;
        _lastServed = lastServed;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Gets the longest a remote may send nothing in the middle of a stream. The idle time of decision 17: a stream with no
    /// bytes is as idle as a session with no requests, and the shorter opening time would cut a healthy film on a remote
    /// whose disks are waking up.
    /// </summary>
    public TimeSpan StallLimit => Times.Idle;

    private PlaybackTimes Times => _store.Read(PlaybackTimes.From);

    /// <summary>
    /// Finds how to serve a <c>PlaybackInfo</c>: the sources of its stub, then those of the other versions the user sees
    /// (D22), then the local transcode of any of them. The outcome names the version that serves.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<PlaybackOutcome> OpenAsync(PlaybackOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // After a fallback the session is under the version that served, which the same client may ask for again.
        var versions = VersionsOf(request).ToList();
        var previous = versions.Select(version => _sessions.FindSameStream(version.Identity)).FirstOrDefault(session => session is not null);

        // jellyfin-web asks again after every drop and never stops by itself (row 57).
        var stubIds = versions.Select(version => version.Stub.Id).ToList();
        if (_failures.Count(request.Identity.UserId, stubIds, _clock.GetUtcNow()) > PlaybackLimits.Reconnections)
        {
            _logger.LogInformation("A title dropped more than {Count} times during playback for a user, its playback is refused for now", PlaybackLimits.Reconnections);
            await ClosePreviousAsync(previous).ConfigureAwait(false);
            return new PlaybackOutcome(PlaybackOutcomeKind.Unavailable, AnyReachable: true);
        }

        if (!await MakeRoomForAsync(request.Identity.UserId, previous).ConfigureAwait(false))
        {
            _logger.LogInformation("A user has {Count} streams running, the most allowed", PlaybackLimits.SessionsPerUser);
            return new PlaybackOutcome(PlaybackOutcomeKind.Unavailable, AnyReachable: true);
        }

        // Stream indexes are in the numbering of the source the client was told about for the version it asks.
        var told = _sessions.FindSameStream(request.Identity)?.Source ?? request.Stub.Sources[0];
        var reachable = new List<(PlaybackOpenRequest Version, StubSource Source)>();
        var refused = 0;
        var tried = 0;
        foreach (var (version, source) in Chain(versions, previous))
        {
            tried++;
            var attempt = await TryRemoteAsync(version, source, source == told, previous, cancellationToken).ConfigureAwait(false);
            if (attempt.Response is not null)
            {
                Remember(version, source);
                await ClosePreviousAsync(previous).ConfigureAwait(false);
                await CloseIfClientLeftAsync(attempt.Response, cancellationToken).ConfigureAwait(false);
                await RecordServedAsync(version, source).ConfigureAwait(false);
                return new PlaybackOutcome(PlaybackOutcomeKind.Remote, attempt.Response, Served: version.Identity);
            }

            _logger.LogInformation("Source on server {ServerId} cannot serve, trying the next step. Reachable: {Reachable}", source.ServerId, attempt.Reachable);
            if (attempt.Reachable)
            {
                reachable.Add((version, source));
            }

            refused += attempt.AtLimit ? 1 : 0;
        }

        foreach (var (version, source) in reachable)
        {
            if (await CanReadFileAsync(source, cancellationToken).ConfigureAwait(false))
            {
                Remember(version, source);
                await ClosePreviousAsync(previous).ConfigureAwait(false);
                await RecordServedAsync(version, source).ConfigureAwait(false);
                return new PlaybackOutcome(PlaybackOutcomeKind.LocalTranscode, FileSource: source, AnyReachable: true, Served: version.Identity);
            }
        }

        await ClosePreviousAsync(previous).ConfigureAwait(false);

        // Only when the limit is the whole story: with any source down or broken, trying later may not help.
        return new PlaybackOutcome(
            PlaybackOutcomeKind.Unavailable,
            AtStreamLimit: refused > 0 && refused == tried,
            AnyReachable: refused > 0 || reachable.Count > 0);
    }

    /// <summary>
    /// Records that the source of a session stopped serving during playback.
    /// </summary>
    /// <param name="session">The session.</param>
    public void ReportFailure(PlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!session.TryFail())
        {
            return;
        }

        var identity = session.Identity;
        _failures.Record(identity.UserId, identity.StubId, session.Source, _clock.GetUtcNow());

        // The .strm would otherwise read first the source that just dropped.
        var key = (identity.UserId, identity.StubId);
        if (_workingSources.TryGetValue(key, out var working) && working.Source == session.Source)
        {
            _workingSources.TryRemove(KeyValuePair.Create(key, working));
        }

        _logger.LogInformation("A source on server {ServerId} stopped serving during playback. It goes last for this user and title", session.Source.ServerId);
    }

    /// <summary>
    /// Sends the subtitles of a local transcode to the remote, through a session of their own: the stub has no subtitle
    /// file, and the local ffmpeg should not read the whole stream to extract one.
    /// </summary>
    /// <param name="identity">Who plays what.</param>
    /// <param name="source">The source of the file.</param>
    /// <param name="stream">The subtitle in the numbering of the remote, its URL still the stock one.</param>
    /// <param name="session">The session of this playback, null to start one.</param>
    /// <param name="localToken">The local token.</param>
    /// <returns>The session.</returns>
    public PlaybackSession MapLocalSubtitle(PlaybackIdentity identity, StubSource source, MediaStream stream, PlaybackSession? session, string localToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stream);

        if (session is null)
        {
            session = new PlaybackSession(identity, source, null, PlaybackStreamKind.Subtitles, string.Empty, _clock.GetUtcNow());
            _sessions.Add(session);
        }

        var format = System.IO.Path.GetExtension(stream.DeliveryUrl?.Split('?')[0]) ?? ".vtt";
        stream.DeliveryUrl = session.MapRootedUrl(
            "/Videos/" + Uri.EscapeDataString(source.ItemId) + "/" + Uri.EscapeDataString(source.MediaSourceId) + "/Subtitles/" + stream.Index.ToString(CultureInfo.InvariantCulture) + "/0/Stream" + format,
            localToken);
        stream.IsExternalUrl = false;
        return session;
    }

    /// <summary>
    /// Opens the remote file of a stub for the server's own reader: the local ffmpeg of the fallback. It has no user, so it
    /// stays on its stub, first the source <c>OpenAsync</c> chose, and a stub of one server never reads another (D22).
    /// </summary>
    /// <param name="stub">The stub.</param>
    /// <param name="method">GET or HEAD.</param>
    /// <param name="range">The <c>Range</c> header, if any.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stream, null if no source gives it.</returns>
    public async Task<RemoteContent?> OpenFileAsync(StubEntry stub, HttpMethod method, string? range, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stub);

        var chosen = ChosenFor(stub);
        IReadOnlyList<StubSource> sources = stub.ServerId is { } server ? stub.Sources.Where(source => source.ServerId == server).ToList() : stub.Sources;
        var refused = 0;
        var tried = 0;
        foreach (var source in Ordered(sources, chosen?.Working.Source))
        {
            tried++;
            var request = new RemoteStreamRequest(method, FilePath(source), range);
            var stream = await OpenAsync(source.ServerId, request, cancellationToken).ConfigureAwait(false);
            if (stream.IsSuccess)
            {
                // Without a playback that chose, the source is kept for the next read, as a source of no user.
                _workingSources[chosen?.Key ?? (Guid.Empty, stub.Id)] = new WorkingSource(source, _clock.GetUtcNow());
                return stream.Value;
            }

            refused += stream.Failure == RemoteFailure.StreamLimitReached ? 1 : 0;
        }

        if (tried > 0 && refused == tried)
        {
            _filesAtLimit[stub.Id] = _clock.GetUtcNow();
        }

        return null;
    }

    /// <summary>
    /// Tells whether the file of a stub was just refused by every source for its stream limit. Nothing holds a slot between
    /// the probe of the local transcode and the read of ffmpeg, so another stream can take it, and all that comes back from
    /// ffmpeg is that it failed. Asked once: the answer is used up.
    /// </summary>
    /// <param name="stubId">The stub.</param>
    /// <returns>Whether the limit explains a failure now.</returns>
    public bool TakeFileRefusal(string stubId)
    {
        return _filesAtLimit.TryRemove(stubId, out var at) && _clock.GetUtcNow() - at < PlaybackTimeouts.FileRefusalMemory;
    }

    /// <summary>
    /// Opens a stream of a session.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="request">What to open.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stream.</returns>
    public async Task<RemoteResult<RemoteContent>> OpenAsync(Guid serverId, RemoteStreamRequest request, CancellationToken cancellationToken)
    {
        var place = _streams.TryEnter(serverId);
        if (place is null)
        {
            return RemoteResult.Fail<RemoteContent>(RemoteFailure.Busy);
        }

        var handedOver = false;
        try
        {
            var opened = await GatedAsync(
                serverId,
                Times.StreamOpen,
                (endpoint, session, token) => _client.OpenStreamAsync(endpoint, session, request, token),
                cancellationToken).ConfigureAwait(false);
            handedOver = opened.IsSuccess;
            return handedOver ? RemoteResult.Success(opened.Value!.HoldUntilDisposed(place)) : opened;
        }
        finally
        {
            if (!handedOver)
            {
                place.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads a playlist of a session.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="path">The path and query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The text.</returns>
    public Task<RemoteResult<string>> ReadPlaylistAsync(Guid serverId, string path, CancellationToken cancellationToken)
    {
        return GatedAsync(
            serverId,
            Times.StreamOpen,
            (endpoint, session, token) => _client.ReadPlaylistAsync(endpoint, session, path, token),
            cancellationToken);
    }

    /// <summary>
    /// Lists the values of a remote that may never reach a client.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>The values.</returns>
    public static IReadOnlyList<string> SecretsOf(PlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return [session.RemotePlaySessionId ?? string.Empty, session.Source.ServerId.ToString("N")];
    }

    /// <summary>
    /// Ends a session: first it becomes unreachable, then the remote transcode is closed.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>A task.</returns>
    public async Task CloseAsync(string sessionId)
    {
        if (_sessions.Remove(sessionId) is { } session)
        {
            await CloseRemoteAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records the stock play session of a stub played on this server, so that only its user can stop it.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="itemId">The stub.</param>
    /// <param name="playSessionId">The play session.</param>
    /// <param name="deviceIds">The devices it runs on.</param>
    public void RecordLocalPlaySession(Guid userId, Guid itemId, string? playSessionId, IEnumerable<string?> deviceIds)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        if (!string.IsNullOrEmpty(playSessionId))
        {
            _sessions.RecordLocal(userId, playSessionId, itemId, [.. deviceIds]);
        }
    }

    /// <summary>
    /// Ends a session reported as stopped, if the report comes from whoever opened it. Stock takes any play session from any
    /// user, and the session of someone else is left alone.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="users">The users behind the report.</param>
    /// <returns>A task.</returns>
    public async Task CloseReportedAsync(string sessionId, IEnumerable<Guid> users)
    {
        ArgumentNullException.ThrowIfNull(users);

        if (_sessions.Find(sessionId) is { } session && users.Contains(session.Identity.UserId))
        {
            await CloseAsync(session.Id).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ends the sessions on a server that is about to be removed: afterwards nothing could reach it to give its slots back.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>A task.</returns>
    public Task CloseServerAsync(Guid serverId)
    {
        return Task.WhenAll(_sessions.All.Where(session => session.Source.ServerId == serverId).Select(session => CloseAsync(session.Id)));
    }

    /// <summary>
    /// Ends every session at shutdown: they live in memory, so after a restart nothing would close their transcodes.
    /// </summary>
    /// <returns>A task.</returns>
    public Task CloseAllAsync()
    {
        return Task.WhenAll(_sessions.All.Select(session => CloseAsync(session.Id)));
    }

    /// <summary>
    /// Renews the stream slots clients are using, closes the transcode of an idle session and keeps the session, or
    /// forgets it when it is too old.
    /// </summary>
    /// <returns>A task.</returns>
    public async Task SweepAsync()
    {
        var now = _clock.GetUtcNow();
        await Task.WhenAll(_sessions.All
            .Where(session => session.RemotePlaySessionId is not null && IsJellyMesh(session.Source.ServerId) && session.TryRenew(now, PlaybackTimeouts.SlotRenewal))
            .Select(RenewSlotAsync)).ConfigureAwait(false);

        var times = Times;
        foreach (var session in _sessions.All.Where(session => !session.IsActive))
        {
            var idle = now - session.LastRequest;
            if (idle >= times.SessionRetention)
            {
                await CloseAsync(session.Id).ConfigureAwait(false);
            }
            else if (idle >= times.Idle && session.TryPark())
            {
                await CloseRemoteAsync(session).ConfigureAwait(false);
            }
        }

        foreach (var entry in _workingSources.Where(entry => now - entry.Value.At >= times.SessionRetention))
        {
            _workingSources.TryRemove(entry);
        }

        _failures.ForgetOld(now);
    }

    /// <summary>
    /// Tells a JellyMesh remote that a client went, which it cannot see once what it sent is in the buffers on the way. The
    /// same as the sweep does for an idle session, sooner: the session stays, and a client that returns asks the remote
    /// again. A failure only leaves the remote to its own idle time.
    /// </summary>
    /// <returns>A task.</returns>
    public Task ReleaseAbandonedAsync()
    {
        var now = _clock.GetUtcNow();
        return Task.WhenAll(_sessions.All
            .Where(session => session.IsAbandoned(now, StreamSlots.AbandonGrace) && session.RemotePlaySessionId is not null && IsJellyMesh(session.Source.ServerId) && session.TryPark())
            .Select(CloseRemoteAsync));
    }

    // The request itself, then one request per other version the user sees, in their order.
    private static IEnumerable<PlaybackOpenRequest> VersionsOf(PlaybackOpenRequest request)
    {
        yield return request;
        foreach (var fallback in request.Fallbacks)
        {
            yield return request with
            {
                Identity = request.Identity with { ItemId = fallback.ItemId, StubId = fallback.Stub.Id },
                Stub = fallback.Stub,
                Fallbacks = []
            };
        }
    }

    // Every source of every version in order, the one of the session replaced first, then those that dropped during playback
    // for this user and title last: the session replaced is often the one that dropped (row 57).
    private List<(PlaybackOpenRequest Version, StubSource Source)> Chain(IReadOnlyList<PlaybackOpenRequest> versions, PlaybackSession? previous)
    {
        var now = _clock.GetUtcNow();
        var steps = versions
            .SelectMany(version => Ordered(version.Stub.Sources, previous?.Source).Select(source => (Version: version, Source: source)))
            .Select(step => (Step: step, Failed: _failures.Failed(step.Version.Identity.UserId, step.Version.Stub.Id, step.Source, now)))
            .ToList();
        return [.. steps.Where(step => !step.Failed).Select(step => step.Step), .. steps.Where(step => step.Failed).Select(step => step.Step)];
    }

    private static IEnumerable<StubSource> Ordered(IReadOnlyList<StubSource> sources, StubSource? first)
    {
        return first is not null && sources.Contains(first)
            ? new[] { first }.Concat(sources.Where(source => source != first))
            : sources;
    }

    private static string FilePath(StubSource source, string? playSessionId = null)
    {
        var path = "/Videos/" + Uri.EscapeDataString(source.ItemId) + "/stream?static=true&MediaSourceId=" + Uri.EscapeDataString(source.MediaSourceId);
        return playSessionId is null ? path : path + "&PlaySessionId=" + Uri.EscapeDataString(playSessionId);
    }

    private static bool IsOffline(RemoteFailure failure)
    {
        return failure is RemoteFailure.Unreachable or RemoteFailure.Timeout or RemoteFailure.Suspended or RemoteFailure.UnknownServer;
    }

    private static RemotePlaybackRequest ToRemote(PlaybackOpenRequest request, StubSource source, bool keepIndexes, string? replaces)
    {
        return new RemotePlaybackRequest
        {
            ItemId = source.ItemId,
            MediaSourceId = source.MediaSourceId,
            DeviceProfile = request.DeviceProfile,
            AudioStreamIndex = keepIndexes ? request.AudioStreamIndex : null,
            SubtitleStreamIndex = keepIndexes ? request.SubtitleStreamIndex : null,
            StartTimeTicks = request.StartTimeTicks,
            MaxStreamingBitrate = request.MaxStreamingBitrate,
            MaxAudioChannels = request.MaxAudioChannels,
            EnableDirectPlay = request.EnableDirectPlay,
            EnableDirectStream = request.EnableDirectStream,
            EnableTranscoding = request.EnableTranscoding,
            AllowVideoStreamCopy = request.AllowVideoStreamCopy,
            AllowAudioStreamCopy = request.AllowAudioStreamCopy,
            AlwaysBurnInSubtitleWhenTranscoding = request.AlwaysBurnInSubtitleWhenTranscoding,
            ReplacesPlaySessionId = replaces
        };
    }

    private static bool PolicyForbids(RemoteTranscodePolicy? policy, string transcodingUrl)
    {
        if (policy is null)
        {
            return false;
        }

        var reasons = transcodingUrl.Split('?', '&').FirstOrDefault(pair => pair.StartsWith("TranscodeReasons=", StringComparison.OrdinalIgnoreCase));
        return reasons is not null
            && ((!policy.EnableVideoTranscoding && reasons.Contains("Video", StringComparison.Ordinal))
                || (!policy.EnableAudioTranscoding && reasons.Contains("Audio", StringComparison.Ordinal)));
    }

    // The value comes from the remote: a line of log must not be something it can write.
    private static string LoggableWord(string? value)
    {
        return value is { Length: > 0 and <= 16 } && value.All(char.IsAsciiLetterOrDigit) ? value : "unknown";
    }

    // A client that left took the play session with it. The slot it holds on a remote would otherwise wait for the sweep.
    private async Task CloseIfClientLeftAsync(PlaybackInfoResponse response, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            await CloseAsync(response.PlaySessionId!).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    // Saved for the migration to the folders per server (D15). Not tied to the client: the answer is already settled.
    private Task RecordServedAsync(PlaybackOpenRequest request, StubSource source)
    {
        return _lastServed.RecordAsync(request.Identity.UserId, request.Stub.Id, source.ServerId, CancellationToken.None);
    }

    // Kept per user and stub (D22): the choice of one user never carries another to a server they do not see.
    private void Remember(PlaybackOpenRequest version, StubSource source)
    {
        _workingSources[(version.Identity.UserId, version.Stub.Id)] = new WorkingSource(source, _clock.GetUtcNow());
    }

    // The source a playback of this stub last chose, by any user: the .strm names none. A stub stays on its server.
    private ((Guid UserId, string StubId) Key, WorkingSource Working)? ChosenFor(StubEntry stub)
    {
        var now = _clock.GetUtcNow();
        var retention = Times.SessionRetention;
        ((Guid UserId, string StubId) Key, WorkingSource Working)? chosen = null;
        foreach (var (key, working) in _workingSources)
        {
            if (string.Equals(key.StubId, stub.Id, StringComparison.Ordinal)
                && now - working.At < retention
                && stub.Sources.Contains(working.Source)
                && (chosen is null || working.At > chosen.Value.Working.At))
            {
                chosen = (key, working);
            }
        }

        return chosen;
    }

    private async Task ClosePreviousAsync(PlaybackSession? previous)
    {
        if (previous is not null)
        {
            await CloseAsync(previous.Id).ConfigureAwait(false);
        }
    }

    // Each session may hold a transcode on a remote, and a client can make one per device identifier it invents. Those
    // not in use go first, the oldest before the others: a parked one costs a remote nothing.
    private async Task<bool> MakeRoomForAsync(Guid userId, PlaybackSession? replaced)
    {
        var sessions = _sessions.All.Where(session => session.Identity.UserId == userId && session != replaced).ToList();
        var excess = sessions.Count - PlaybackLimits.SessionsPerUser + 1;
        if (excess <= 0)
        {
            return true;
        }

        var unused = sessions.Where(session => !session.IsActive).OrderBy(session => session.LastRequest).Take(excess).ToList();
        foreach (var session in unused)
        {
            await CloseAsync(session.Id).ConfigureAwait(false);
        }

        return unused.Count == excess;
    }

    private bool IsJellyMesh(Guid serverId)
    {
        return _registry.GetServers().Any(server => server.Id == serverId && server.Mode == RemoteMode.JellyMesh);
    }

    private async Task<Attempt> TryRemoteAsync(PlaybackOpenRequest request, StubSource source, bool keepIndexes, PlaybackSession? previous, CancellationToken cancellationToken)
    {
        // A new PlaybackInfo on the same server takes over the slot of the stream it replaces: on a full server a change of
        // audio track would otherwise be refused by its own stream.
        var replaces = previous?.Source.ServerId == source.ServerId && IsJellyMesh(source.ServerId) ? previous.RemotePlaySessionId : null;
        var remoteRequest = ToRemote(request, source, keepIndexes, replaces);
        var answer = await GatedAsync(
            source.ServerId,
            Times.RemotePlaybackInfo,
            (endpoint, session, token) => _client.GetPlaybackInfoAsync(endpoint, session, remoteRequest, token),
            cancellationToken).ConfigureAwait(false);
        if (answer.Failure == RemoteFailure.StreamLimitReached)
        {
            // Not worth a place in the local fallback: reading its file is a stream too, and it would be refused the same.
            _logger.LogInformation("Server {ServerId} is at its stream limit for this server", source.ServerId);
            return new Attempt(null, false, true);
        }

        if (!answer.IsSuccess)
        {
            return new Attempt(null, !IsOffline(answer.Failure));
        }

        var info = answer.Value!;
        var remote = info.MediaSources.FirstOrDefault(candidate => candidate.Id == source.MediaSourceId) ?? (info.MediaSources.Count > 0 ? info.MediaSources[0] : null);
        if (info.PlaySessionId is null)
        {
            return new Attempt(null, true);
        }

        if (info.ErrorCode is not null || remote is null)
        {
            await ReleaseSlotAsync(source.ServerId, info.PlaySessionId).ConfigureAwait(false);
            return new Attempt(null, true);
        }

        PlaybackSession? session = null;
        try
        {
            session = await OpenStreamKindAsync(request, source, remote, info.PlaySessionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Also for a client that left during the probe: nothing else knows of this slot.
            if (session is null)
            {
                await ReleaseSlotAsync(source.ServerId, info.PlaySessionId).ConfigureAwait(false);
            }
        }

        if (session is null)
        {
            return new Attempt(null, true);
        }

        var built = PlaybackResponseBuilder.Build(remote, session, request.LocalToken);
        if (built is null)
        {
            await CloseRemoteAsync(session).ConfigureAwait(false);
            return new Attempt(null, true);
        }

        _sessions.Add(session);
        return new Attempt(new PlaybackInfoResponse { MediaSources = [built], PlaySessionId = session.Id }, true);
    }

    private async Task<PlaybackSession?> OpenStreamKindAsync(PlaybackOpenRequest request, StubSource source, MediaSourceInfo remote, string remotePlaySessionId, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        if (remote.SupportsDirectPlay)
        {
            // A JellyMesh remote counts this stream by its play session. A stock request stays as it always was.
            var path = FilePath(source, IsJellyMesh(source.ServerId) ? remotePlaySessionId : null);
            return await CanOpenAsync(source.ServerId, new RemoteStreamRequest(HttpMethod.Get, path, ProbeRange), cancellationToken).ConfigureAwait(false)
                ? new PlaybackSession(request.Identity, source, remotePlaySessionId, PlaybackStreamKind.Direct, path, now)
                : null;
        }

        if (string.IsNullOrEmpty(remote.TranscodingUrl) || !remote.SupportsTranscoding)
        {
            return null;
        }

        var policy = await GetPolicyAsync(source.ServerId, cancellationToken).ConfigureAwait(false);
        if (PolicyForbids(policy, remote.TranscodingUrl))
        {
            return null;
        }

        var upstream = PlaybackUrls.WithoutToken(remote.TranscodingUrl);
        var kind = remote.TranscodingSubProtocol == MediaStreamProtocol.hls ? PlaybackStreamKind.Hls : PlaybackStreamKind.Progressive;
        var session = new PlaybackSession(request.Identity, source, remotePlaySessionId, kind, upstream, now);
        try
        {
            var works = kind == PlaybackStreamKind.Hls
                ? await FirstSegmentWorksAsync(session, request.DeviceProfile, cancellationToken).ConfigureAwait(false)
                : await CanOpenAsync(source.ServerId, new RemoteStreamRequest(HttpMethod.Get, upstream, null), cancellationToken).ConfigureAwait(false);
            if (works)
            {
                return session;
            }
        }
        catch (OperationCanceledException)
        {
            // The client left during the probe, and nothing else would close what it started.
            await StopEncodingAsync(session).ConfigureAwait(false);
            throw;
        }

        await StopEncodingAsync(session).ConfigureAwait(false);
        return null;
    }

    private async Task<bool> FirstSegmentWorksAsync(PlaybackSession session, DeviceProfile? profile, CancellationToken cancellationToken)
    {
        var master = await ReadPlaylistAsync(session.Source.ServerId, session.UpstreamPath, cancellationToken).ConfigureAwait(false);
        if (!master.IsSuccess)
        {
            return false;
        }

        var variant = HlsMaster.ReadVariants(master.Value!).FirstOrDefault(candidate => HlsMaster.IsPlayable(candidate, profile));
        if (variant is null)
        {
            _logger.LogInformation("A remote offered codecs the client cannot play");
            return false;
        }

        var folder = session.UpstreamFolder();
        var variantPath = folder + variant.Uri;
        var playlist = await ReadPlaylistAsync(session.Source.ServerId, variantPath, cancellationToken).ConfigureAwait(false);
        if (!playlist.IsSuccess || HlsMaster.FirstSegment(playlist.Value!) is not { } segment)
        {
            return false;
        }

        var segmentFolder = variantPath.Split('?')[0];
        segmentFolder = segmentFolder[..(segmentFolder.LastIndexOf('/') + 1)];
        return await CanOpenAsync(session.Source.ServerId, new RemoteStreamRequest(HttpMethod.Get, segmentFolder + segment, null), cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> CanReadFileAsync(StubSource source, CancellationToken cancellationToken)
    {
        return CanOpenAsync(source.ServerId, new RemoteStreamRequest(HttpMethod.Get, FilePath(source), ProbeRange), cancellationToken);
    }

    private async Task<bool> CanOpenAsync(Guid serverId, RemoteStreamRequest request, CancellationToken cancellationToken)
    {
        var stream = await OpenAsync(serverId, request with { IsProbe = IsJellyMesh(serverId) }, cancellationToken).ConfigureAwait(false);
        stream.Value?.Dispose();
        return stream.IsSuccess;
    }

    private async Task<RemoteTranscodePolicy?> GetPolicyAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var times = Times;
        if (_policies.TryGetValue(serverId, out var cached) && now - cached.ReadAt < times.PolicyRetention)
        {
            return cached.Policy;
        }

        var readHardware = !IsJellyMesh(serverId);
        var policy = await GatedAsync(
            serverId,
            times.RemotePlaybackInfo,
            (endpoint, session, token) => _client.GetTranscodePolicyAsync(endpoint, session, readHardware, token),
            cancellationToken).ConfigureAwait(false);
        if (!policy.IsSuccess)
        {
            return null;
        }

        _policies[serverId] = new CachedPolicy(policy.Value!, now);
        _logger.LogInformation("Remote hardware acceleration: {HardwareAcceleration}", LoggableWord(policy.Value!.HardwareAccelerationType));
        return policy.Value;
    }

    private async Task CloseRemoteAsync(PlaybackSession session)
    {
        await StopEncodingAsync(session).ConfigureAwait(false);
        if (session.RemotePlaySessionId is { } playSessionId)
        {
            await ReleaseSlotAsync(session.Source.ServerId, playSessionId).ConfigureAwait(false);
        }
    }

    // A lost renewal is covered by the next one, and a provider without this route only keeps its idle time.
    private async Task RenewSlotAsync(PlaybackSession session)
    {
        var result = await WithinAsync(
            session.Source.ServerId,
            PlaybackTimeouts.RemoteStop,
            (endpoint, remote, token) => _client.RenewStreamAsync(endpoint, remote, session.RemotePlaySessionId!, token),
            CancellationToken.None).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.LogInformation("Could not renew a stream slot: {Failure}", result.Failure);
        }
    }

    // A failure here costs nothing but time: the remote frees the slot by itself.
    private async Task ReleaseSlotAsync(Guid serverId, string playSessionId)
    {
        if (!IsJellyMesh(serverId))
        {
            return;
        }

        var result = await WithinAsync(
            serverId,
            PlaybackTimeouts.RemoteStop,
            (endpoint, remote, token) => _client.ReleaseStreamAsync(endpoint, remote, playSessionId, token),
            CancellationToken.None).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.LogInformation("Could not give a stream slot back: {Failure}", result.Failure);
        }
    }

    private async Task StopEncodingAsync(PlaybackSession session)
    {
        if (session.Kind == PlaybackStreamKind.Direct || session.RemotePlaySessionId is not { } playSessionId)
        {
            return;
        }

        var result = await WithinAsync(
            session.Source.ServerId,
            PlaybackTimeouts.RemoteStop,
            (endpoint, remote, token) => _client.StopEncodingAsync(endpoint, remote, playSessionId, token),
            CancellationToken.None).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.LogWarning("Could not close a remote transcode: {Failure}", result.Failure);
        }
    }

    // For what a client can multiply. Closing what is open stays outside: it is what frees a remote, and it is bounded by
    // the sessions that exist.
    private async Task<RemoteResult<T>> GatedAsync<T>(
        Guid serverId,
        TimeSpan timeout,
        Func<RemoteEndpoint, RemoteSession, CancellationToken, Task<RemoteResult<T>>> call,
        CancellationToken cancellationToken)
    {
        var gate = _callGates.GetOrAdd(serverId, _ => new SemaphoreSlim(PlaybackLimits.CallsPerServer, PlaybackLimits.CallsPerServer));
        if (!await gate.WaitAsync(PlaybackLimits.QueueWait, cancellationToken).ConfigureAwait(false))
        {
            return RemoteResult.Fail<T>(RemoteFailure.Busy);
        }

        try
        {
            return await WithinAsync(serverId, timeout, call, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RemoteResult<T>> WithinAsync<T>(
        Guid serverId,
        TimeSpan timeout,
        Func<RemoteEndpoint, RemoteSession, CancellationToken, Task<RemoteResult<T>>> call,
        CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            return await _registry.RunPlaybackCallAsync(serverId, call, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RemoteResult.Fail<T>(RemoteFailure.Timeout);
        }
    }

    private sealed record Attempt(PlaybackInfoResponse? Response, bool Reachable, bool AtLimit = false);

    private sealed record WorkingSource(StubSource Source, DateTimeOffset At);

    private sealed record CachedPolicy(RemoteTranscodePolicy Policy, DateTimeOffset ReadAt);
}
