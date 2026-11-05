using System;
using System.Threading;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// A stream request holding a slot of <see cref="StreamSlots"/>. Dispose it when the request ends.
/// </summary>
public sealed class StreamRequest : IDisposable
{
    private readonly Action<bool> _end;
    private int _ended;
    private volatile bool _abandoned;

    internal StreamRequest(Action<bool> end)
    {
        _end = end;
    }

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
            _end(_abandoned);
        }
    }
}
