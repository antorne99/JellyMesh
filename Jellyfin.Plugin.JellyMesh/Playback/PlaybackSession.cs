using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Jellyfin.Plugin.JellyMesh.Library;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// One stream of one client. Everything about the remote stays in here: a client only ever sees <see cref="Id"/>, and
/// URLs made of that and of opaque numbers.
/// </summary>
public sealed partial class PlaybackSession
{
    // Segment timing, the only part of a remote query a client may see and send back.
    private static readonly HashSet<string> _clientParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "runtimeTicks",
        "actualSegmentLengthTicks",
        "StartPositionTicks",
        "EndPositionTicks",
        "SegmentLength",
        "CopyTimestamps",
        "AddVttTimeMap"
    };

    private readonly ConcurrentDictionary<string, int> _folderIndexes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, string> _folders = new();
    private readonly ConcurrentDictionary<string, string> _queries = new(StringComparer.Ordinal);
    private long _lastRequestTicks;
    private long _lastRenewalTicks;
    private int _activeRequests;
    private int _parked;
    private int _abandoned;
    private int _failed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSession"/> class.
    /// </summary>
    /// <param name="identity">Who plays what.</param>
    /// <param name="source">The source on the remote.</param>
    /// <param name="remotePlaySessionId">The play session on the remote.</param>
    /// <param name="kind">How the stream reaches the client.</param>
    /// <param name="upstreamPath">The master playlist or the stream on the remote, without a token.</param>
    /// <param name="now">The current time.</param>
    public PlaybackSession(PlaybackIdentity identity, StubSource source, string? remotePlaySessionId, PlaybackStreamKind kind, string upstreamPath, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(upstreamPath);

        Id = Guid.NewGuid().ToString("N");
        Identity = identity;
        Source = source;
        RemotePlaySessionId = remotePlaySessionId;
        Kind = kind;
        UpstreamPath = upstreamPath;
        _lastRequestTicks = now.UtcTicks;
        _lastRenewalTicks = now.UtcTicks;
    }

    /// <summary>Gets the opaque identifier, which is also the local <c>PlaySessionId</c>.</summary>
    public string Id { get; }

    /// <summary>Gets who plays what.</summary>
    public PlaybackIdentity Identity { get; }

    /// <summary>Gets the source on the remote.</summary>
    public StubSource Source { get; }

    /// <summary>Gets the play session on the remote. Never sent to a client.</summary>
    public string? RemotePlaySessionId { get; }

    /// <summary>Gets how the stream reaches the client.</summary>
    public PlaybackStreamKind Kind { get; }

    /// <summary>Gets the master playlist or the stream on the remote, without a token.</summary>
    public string UpstreamPath { get; }

    /// <summary>Gets the time of the last request.</summary>
    public DateTimeOffset LastRequest => new(Interlocked.Read(ref _lastRequestTicks), TimeSpan.Zero);

    /// <summary>Gets a value indicating whether a request is being served.</summary>
    public bool IsActive => Volatile.Read(ref _activeRequests) > 0;

    /// <summary>
    /// Gets a value indicating whether the source of this session stopped serving during playback.
    /// </summary>
    public bool HasFailed => Volatile.Read(ref _failed) == 1;

    /// <summary>
    /// Finds the folder of the master playlist or stream on the remote.
    /// </summary>
    /// <returns>The folder, ending with a slash.</returns>
    public string UpstreamFolder()
    {
        var plain = UpstreamPath.Split('?')[0];
        return plain[..(plain.LastIndexOf('/') + 1)];
    }

    /// <summary>
    /// Records a request. Dispose the result when it ends.
    /// </summary>
    /// <param name="clock">The clock.</param>
    /// <returns>The end of the request.</returns>
    public IDisposable BeginRequest(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        Interlocked.Exchange(ref _lastRequestTicks, clock.GetUtcNow().UtcTicks);
        Interlocked.Exchange(ref _parked, 0);
        Interlocked.Increment(ref _activeRequests);
        return new RequestScope(this, clock, CancellationToken.None);
    }

    /// <summary>
    /// Records a request, unless this session already has <see cref="PlaybackLimits.RequestsPerSession"/> running. Dispose
    /// the result when it ends.
    /// </summary>
    /// <param name="clock">The clock.</param>
    /// <param name="aborted">What tells that the connection of the request went.</param>
    /// <returns>The end of the request, null if there are too many.</returns>
    public IDisposable? TryBeginRequest(TimeProvider clock, CancellationToken aborted = default)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Interlocked.Increment(ref _activeRequests) > PlaybackLimits.RequestsPerSession)
        {
            Interlocked.Decrement(ref _activeRequests);
            return null;
        }

        Interlocked.Exchange(ref _lastRequestTicks, clock.GetUtcNow().UtcTicks);
        Interlocked.Exchange(ref _parked, 0);
        return new RequestScope(this, clock, aborted);
    }

    /// <summary>
    /// Marks the transcode as closed by the sweeper. The session stays: a later request restarts ffmpeg on the remote.
    /// </summary>
    /// <returns>Whether this call parked it.</returns>
    public bool TryPark() => Interlocked.Exchange(ref _parked, 1) == 0;

    /// <summary>
    /// Marks the source of this session as stopped, once: the retries of a player count as one failure.
    /// </summary>
    /// <returns>Whether this call marked it.</returns>
    public bool TryFail() => Interlocked.Exchange(ref _failed, 1) == 0;

    /// <summary>
    /// Tells whether the client went: its last request lost the connection and no other has come for the whole grace. A
    /// seek drops a connection too, and a pause drops none.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="grace">The time a client has to come back.</param>
    /// <returns>Whether the client is gone.</returns>
    public bool IsAbandoned(DateTimeOffset now, TimeSpan grace)
    {
        return Volatile.Read(ref _abandoned) != 0 && !IsActive && now - LastRequest >= grace;
    }

    /// <summary>
    /// Tells whether the stream slot of this session is due for a renewal, and if so counts it as done. Only while a
    /// request is being served: a pause has none, and its slot goes with the sweep (decision 6).
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="interval">The time between renewals.</param>
    /// <returns>Whether to renew now.</returns>
    public bool TryRenew(DateTimeOffset now, TimeSpan interval)
    {
        if (!IsActive || now.UtcTicks - Interlocked.Read(ref _lastRenewalTicks) < interval.Ticks)
        {
            return false;
        }

        Interlocked.Exchange(ref _lastRenewalTicks, now.UtcTicks);
        return true;
    }

    /// <summary>
    /// Turns a URI of a remote playlist into a URL for the client, keeping the remote form here.
    /// </summary>
    /// <param name="uri">The URI in the playlist.</param>
    /// <param name="remoteFolder">The folder of the playlist on the remote, ending with a slash.</param>
    /// <param name="prefix">What leads from the client's URL of the playlist to the folder route.</param>
    /// <param name="localToken">The local token, which goes in the URL because players send no header.</param>
    /// <returns>The URL, null when the URI cannot be accepted.</returns>
    public string? MapRelativeUri(string uri, string remoteFolder, string prefix, string localToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(remoteFolder);

        return uri.StartsWith('/') ? null : Map(uri, remoteFolder, prefix, localToken);
    }

    /// <summary>
    /// Turns a server-relative URL of a remote into a URL for the client.
    /// </summary>
    /// <param name="url">The URL on the remote, such as a subtitle <c>DeliveryUrl</c>.</param>
    /// <param name="localToken">The local token.</param>
    /// <returns>The URL, null when it cannot be accepted.</returns>
    public string? MapRootedUrl(string url, string localToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        return url.StartsWith('/') ? Map(url, string.Empty, PlaybackUrls.FolderPrefix(Id), localToken) : null;
    }

    /// <summary>
    /// Finds the remote form of a URL handed out by this session.
    /// </summary>
    /// <param name="folder">The folder number.</param>
    /// <param name="file">The file name.</param>
    /// <param name="clientQuery">The query of the client.</param>
    /// <returns>The path and query on the remote, null if this session never handed it out.</returns>
    public string? FindUpstream(int folder, string file, IEnumerable<KeyValuePair<string, string>> clientQuery)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(clientQuery);

        if (!IsSafeFileName(file) || !_folders.TryGetValue(folder, out var path) || !_queries.TryGetValue(ShapeOf(folder, file), out var query))
        {
            return null;
        }

        var parameters = new List<string>();
        if (query.Length > 0)
        {
            parameters.Add(query);
        }

        parameters.AddRange(clientQuery
            .Where(pair => _clientParameters.Contains(pair.Key))
            .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

        return path + file + (parameters.Count > 0 ? "?" + string.Join('&', parameters) : string.Empty);
    }

    [GeneratedRegex(@"^-?\d+\.")]
    private static partial Regex NumberedFile();

    private static bool IsSafeFileName(string file)
    {
        return file.Length > 0 && file != "." && file != ".." && file.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
    }

    // The timing parameters are numbers and flags, and this is what a remote can get into a URL a client receives.
    private static bool IsPlainValue(string pair)
    {
        return pair.All(character => char.IsAsciiLetterOrDigit(character) || character is '=' or '.' or '-');
    }

    private static string ShapeOf(int folder, string file)
    {
        // jellyfin-web asks for an external subtitle in its own format: Stream.js for a Stream.vtt it was given.
        var shape = file.StartsWith("Stream.", StringComparison.OrdinalIgnoreCase) ? "Stream" : NumberedFile().Replace(file, "N.");
        return string.Create(CultureInfo.InvariantCulture, $"{folder}/{shape}");
    }

    // What a remote playlist can make this session remember. Numbered segments share one entry, so a film needs a handful
    // of them, and a remote that makes up new names at every read would otherwise grow this without end.
    private bool HasRoomFor(string folder, string file)
    {
        return (_folderIndexes.ContainsKey(folder) || _folderIndexes.Count < PlaybackLimits.FoldersPerSession)
            && _queries.Count < PlaybackLimits.FileShapesPerSession;
    }

    private string? Map(string uri, string remoteFolder, string prefix, string localToken)
    {
        var mark = uri.IndexOf('?', StringComparison.Ordinal);
        var path = mark < 0 ? uri : uri[..mark];
        var query = mark < 0 ? string.Empty : uri[(mark + 1)..];

        if (path.Contains("://", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        var full = remoteFolder + path;
        var slash = full.LastIndexOf('/');
        if (slash < 0 || full.Split('/').Contains(".."))
        {
            return null;
        }

        var folder = full[..(slash + 1)];
        var file = full[(slash + 1)..];
        if (!IsSafeFileName(file) || !HasRoomFor(folder, file))
        {
            return null;
        }

        var index = _folderIndexes.GetOrAdd(folder, _ =>
        {
            var next = _folders.Count;
            while (!_folders.TryAdd(next, folder))
            {
                next++;
            }

            return next;
        });

        var kept = new List<string>();
        var client = new StringBuilder();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = pair.Split('=', 2)[0];
            if (name.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) || name.Equals("api_key", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_clientParameters.Contains(name) && IsPlainValue(pair))
            {
                client.Append(pair).Append('&');
            }
            else
            {
                kept.Add(pair);
            }
        }

        _queries[ShapeOf(index, file)] = string.Join('&', kept);

        client.Append("ApiKey=").Append(Uri.EscapeDataString(localToken));
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}{index}/{file}?{client}");
    }

    private sealed class RequestScope : IDisposable
    {
        private readonly PlaybackSession _session;
        private readonly TimeProvider _clock;
        private readonly CancellationToken _aborted;
        private int _ended;

        public RequestScope(PlaybackSession session, TimeProvider clock, CancellationToken aborted)
        {
            _session = session;
            _clock = clock;
            _aborted = aborted;
        }

        // A long stream counts as a request until its end, so the idle time starts there, and the last request to end
        // decides whether the client went, as on the provider.
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                Interlocked.Exchange(ref _session._lastRequestTicks, _clock.GetUtcNow().UtcTicks);
                Interlocked.Exchange(ref _session._abandoned, _aborted.IsCancellationRequested ? 1 : 0);
                Interlocked.Decrement(ref _session._activeRequests);
            }
        }
    }
}
