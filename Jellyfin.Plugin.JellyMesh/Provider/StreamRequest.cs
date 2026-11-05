using System;
using System.Threading;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// A stream request holding a slot of <see cref="StreamSlots"/>. Dispose it when the request ends.
/// </summary>
public sealed class StreamRequest : IDisposable
{
    private readonly Action<StreamRequest> _end;
    private int _ended;
    private volatile bool _abandoned;

    internal StreamRequest(string? playSessionId, Guid itemId, Action? cut, Action<StreamRequest> end)
    {
        PlaySessionId = playSessionId;
        ItemId = itemId;
        Cut = cut;
        _end = end;
    }

    internal string? PlaySessionId { get; }

    internal Guid ItemId { get; }

    internal Action? Cut { get; }

    internal bool IsAbandoned => _abandoned;

    /// <summary>
    /// Marks the request as one that did not end well: the connection went, or the stream failed.
    /// </summary>
    public void Abandon()
    {
        _abandoned = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            _end(this);
        }
    }
}
