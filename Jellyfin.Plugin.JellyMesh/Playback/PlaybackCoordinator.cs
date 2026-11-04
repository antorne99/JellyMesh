using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
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
    private readonly TimeProvider _clock;
    private readonly ILogger<PlaybackCoordinator> _logger;
    private readonly ConcurrentDictionary<string, StubSource> _workingSources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, CachedPolicy> _policies = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackCoordinator"/> class.
    /// </summary>
    /// <param name="registry">The server registry.</param>
    /// <param name="client">The playback client.</param>
    /// <param name="sessions">The sessions.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackCoordinator(
        IRemoteServerRegistry registry,
        IRemotePlaybackClient client,
        PlaybackSessionStore sessions,
        TimeProvider clock,
        ILogger<PlaybackCoordinator> logger)
    {
        _registry = registry;
        _client = client;
        _sessions = sessions;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Finds how to serve a <c>PlaybackInfo</c>.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<PlaybackOutcome> OpenAsync(PlaybackOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var previous = _sessions.FindSameStream(request.Identity);
        var reachable = new List<StubSource>();
        foreach (var source in Ordered(request.Stub.Sources, previous?.Source))
        {
            // Stream indexes are in the numbering of the source the client was told about.
            var keepIndexes = source == (previous?.Source ?? request.Stub.Sources[0]);
            var attempt = await TryRemoteAsync(request, source, keepIndexes, cancellationToken).ConfigureAwait(false);
            if (attempt.Response is not null)
            {
                _workingSources[request.Stub.Id] = source;
                await ClosePreviousAsync(previous).ConfigureAwait(false);
                return new PlaybackOutcome(PlaybackOutcomeKind.Remote, attempt.Response);
            }

            _logger.LogInformation("Source on server {ServerId} cannot serve, trying the next step. Reachable: {Reachable}", source.ServerId, attempt.Reachable);
            if (attempt.Reachable)
            {
                reachable.Add(source);
            }
        }

        foreach (var source in reachable)
        {
            if (await CanReadFileAsync(source, cancellationToken).ConfigureAwait(false))
            {
                _workingSources[request.Stub.Id] = source;
                await ClosePreviousAsync(previous).ConfigureAwait(false);
                return new PlaybackOutcome(PlaybackOutcomeKind.LocalTranscode, FileSource: source);
            }
        }

        await ClosePreviousAsync(previous).ConfigureAwait(false);
        return new PlaybackOutcome(PlaybackOutcomeKind.Unavailable);
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
    /// Opens the remote file of a stub for the server's own reader: the local ffmpeg of the fallback.
    /// </summary>
    /// <param name="stub">The stub.</param>
    /// <param name="method">GET or HEAD.</param>
    /// <param name="range">The <c>Range</c> header, if any.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stream, null if no source gives it.</returns>
    public async Task<RemoteContent?> OpenFileAsync(StubEntry stub, HttpMethod method, string? range, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stub);

        _workingSources.TryGetValue(stub.Id, out var working);
        foreach (var source in Ordered(stub.Sources, working))
        {
            var request = new RemoteStreamRequest(method, FilePath(source), range);
            var stream = await OpenAsync(source.ServerId, request, cancellationToken).ConfigureAwait(false);
            if (stream.IsSuccess)
            {
                _workingSources[stub.Id] = source;
                return stream.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Opens a stream of a session.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="request">What to open.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stream.</returns>
    public Task<RemoteResult<RemoteContent>> OpenAsync(Guid serverId, RemoteStreamRequest request, CancellationToken cancellationToken)
    {
        return WithinAsync(
            serverId,
            PlaybackTimeouts.StreamOpen,
            (endpoint, session, token) => _client.OpenStreamAsync(endpoint, session, request, token),
            cancellationToken);
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
        return WithinAsync(
            serverId,
            PlaybackTimeouts.StreamOpen,
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
            await StopEncodingAsync(session).ConfigureAwait(false);
        }
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
    /// Closes the transcode of an idle session and keeps the session, or forgets it when it is too old.
    /// </summary>
    /// <returns>A task.</returns>
    public async Task SweepAsync()
    {
        var now = _clock.GetUtcNow();
        foreach (var session in _sessions.All.Where(session => !session.IsActive))
        {
            var idle = now - session.LastRequest;
            if (idle >= PlaybackTimeouts.SessionRetention)
            {
                await CloseAsync(session.Id).ConfigureAwait(false);
            }
            else if (idle >= PlaybackTimeouts.Idle && session.TryPark())
            {
                await StopEncodingAsync(session).ConfigureAwait(false);
            }
        }
    }

    private static IEnumerable<StubSource> Ordered(IReadOnlyList<StubSource> sources, StubSource? first)
    {
        return first is not null && sources.Contains(first)
            ? new[] { first }.Concat(sources.Where(source => source != first))
            : sources;
    }

    private static string FilePath(StubSource source)
    {
        return "/Videos/" + Uri.EscapeDataString(source.ItemId) + "/stream?static=true&MediaSourceId=" + Uri.EscapeDataString(source.MediaSourceId);
    }

    private static bool IsOffline(RemoteFailure failure)
    {
        return failure is RemoteFailure.Unreachable or RemoteFailure.Timeout or RemoteFailure.Suspended or RemoteFailure.UnknownServer;
    }

    private static RemotePlaybackRequest ToRemote(PlaybackOpenRequest request, StubSource source, bool keepIndexes)
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
            AlwaysBurnInSubtitleWhenTranscoding = request.AlwaysBurnInSubtitleWhenTranscoding
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

    private async Task ClosePreviousAsync(PlaybackSession? previous)
    {
        if (previous is not null)
        {
            await CloseAsync(previous.Id).ConfigureAwait(false);
        }
    }

    private async Task<Attempt> TryRemoteAsync(PlaybackOpenRequest request, StubSource source, bool keepIndexes, CancellationToken cancellationToken)
    {
        var remoteRequest = ToRemote(request, source, keepIndexes);
        var answer = await WithinAsync(
            source.ServerId,
            PlaybackTimeouts.RemotePlaybackInfo,
            (endpoint, session, token) => _client.GetPlaybackInfoAsync(endpoint, session, remoteRequest, token),
            cancellationToken).ConfigureAwait(false);
        if (!answer.IsSuccess)
        {
            return new Attempt(null, !IsOffline(answer.Failure));
        }

        var info = answer.Value!;
        var remote = info.MediaSources.FirstOrDefault(candidate => candidate.Id == source.MediaSourceId) ?? (info.MediaSources.Count > 0 ? info.MediaSources[0] : null);
        if (info.ErrorCode is not null || info.PlaySessionId is null || remote is null)
        {
            return new Attempt(null, true);
        }

        var session = await OpenStreamKindAsync(request, source, remote, info.PlaySessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return new Attempt(null, true);
        }

        var built = PlaybackResponseBuilder.Build(remote, session, request.LocalToken);
        if (built is null)
        {
            await StopEncodingAsync(session).ConfigureAwait(false);
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
            return await CanReadFileAsync(source, cancellationToken).ConfigureAwait(false)
                ? new PlaybackSession(request.Identity, source, remotePlaySessionId, PlaybackStreamKind.Direct, FilePath(source), now)
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
        var stream = await OpenAsync(serverId, request, cancellationToken).ConfigureAwait(false);
        stream.Value?.Dispose();
        return stream.IsSuccess;
    }

    private async Task<RemoteTranscodePolicy?> GetPolicyAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        if (_policies.TryGetValue(serverId, out var cached) && now - cached.ReadAt < PlaybackTimeouts.PolicyRetention)
        {
            return cached.Policy;
        }

        var policy = await WithinAsync(serverId, PlaybackTimeouts.RemotePlaybackInfo, _client.GetTranscodePolicyAsync, cancellationToken).ConfigureAwait(false);
        if (!policy.IsSuccess)
        {
            return null;
        }

        _policies[serverId] = new CachedPolicy(policy.Value!, now);
        _logger.LogInformation("Remote hardware acceleration: {HardwareAcceleration}", policy.Value!.HardwareAccelerationType);
        return policy.Value;
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
            return await _registry.RunWithSessionAsync(serverId, call, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RemoteResult.Fail<T>(RemoteFailure.Timeout);
        }
    }

    private sealed record Attempt(PlaybackInfoResponse? Response, bool Reachable);

    private sealed record CachedPolicy(RemoteTranscodePolicy Policy, DateTimeOffset ReadAt);
}
